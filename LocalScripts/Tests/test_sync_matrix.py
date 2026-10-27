"""Protocol/oracle tests and real private HTTP faults. No native Unity results."""
import copy
import json
from pathlib import Path
import socket
import sys
import tempfile
import unittest
from urllib.error import HTTPError
from urllib.request import Request,urlopen
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import run_sync_matrix as m


def point(fid):return {'type':'Feature','id':fid,'properties':{'id':fid,'class':'fire','vendor':{'x':[1,None]}},'geometry':{'type':'Point','coordinates':[23,38,100]}}
def state(features):return {'type':'FeatureCollection','features':features,'_ar2Sync':{'schemaVersion':1,'pending':[],'remoteIds':{f['id']:f['id'] for f in features},'acknowledgedHashes':{}}}

class MatrixTests(unittest.TestCase):
    def test_restart_uses_real_unity_state_names_and_order(self):
        events=[{'state':s} for s in ('ExitingPlayMode','EnteredEditMode','ExitingEditMode','EnteredPlayMode')]
        m.require_restart_lifecycle(events)
        for states in (('ExitingPlayMode',),('EnteredPlayMode','EnteredEditMode'),('ExitedPlayMode','EnteredPlayMode')):
            with self.assertRaises(m.p.existing.TestFailure):
                m.require_restart_lifecycle([{'state':s} for s in states])

    def test_offline_oracle_requires_exact_payload_baseline_and_revision(self):
        b=state([point('target'),point('other')]);b['_ar2Sync'].update(nextRevision=1,acknowledgedHashes={'target':'old'})
        e=copy.deepcopy(b);e['features'][0]['geometry']['coordinates'][2]+=1
        off=copy.deepcopy(e);off['_ar2Sync'].update(nextRevision=2,pending=[{'kind':'update','featureId':'target','baseHash':'old','payload':copy.deepcopy(e['features'][0])}])
        self.assertTrue(m.offline_oracle('play_restart',b,e,off,'target',None))
        for key,value in [('featureId','other'),('baseHash','wrong'),('payload',point('target'))]:
            bad=copy.deepcopy(off);bad['_ar2Sync']['pending'][0][key]=value
            with self.assertRaises(m.p.existing.TestFailure):m.offline_oracle('play_restart',b,e,bad,'target',None)

    def test_update_requires_exact_unrelated_data_and_empty_outbox(self):
        b=state([point('target'),point('other')]);e=copy.deepcopy(b);e['features'][0]['geometry']['coordinates'][0]+=.001
        self.assertTrue(m.final_oracle('play_restart',b,e,e,e,'target',None))
        for mode in ('loss','pending','property'):
            bad=copy.deepcopy(e)
            if mode=='loss':bad['features'].pop()
            elif mode=='pending':bad['_ar2Sync']['pending']=[{'kind':'update'}]
            else:bad['features'][1]['properties']['vendor']['x'].append(2)
            with self.assertRaises(m.p.existing.TestFailure):m.final_oracle('play_restart',b,e,bad,e,'target',None)

    def test_creation_accepts_only_identity_aliases_and_one_record(self):
        b=state([point('target'),point('other')]);e=copy.deepcopy(b);e['features'].append(point('new'))
        l=copy.deepcopy(e);l['features']=[f for f in l['features'] if f['id']!='target'];l['_ar2Sync']['remoteIds']={'other':'other','new':'server_new'}
        r=copy.deepcopy(l);r['features'][-1]['id']='server_new';r['features'][-1]['properties']['id']='server_new';r['features'][-1]['properties']['ar2_client_id']='new'
        self.assertTrue(m.final_oracle('offline_create_delete',b,e,l,r,'target','new'))
        r['features'].append(copy.deepcopy(r['features'][-1]))
        with self.assertRaises(m.p.existing.TestFailure):m.final_oracle('offline_create_delete',b,e,l,r,'target','new')

    def test_conflicts_require_server_wins_and_preserve_all_other_data(self):
        b=state([point('target'),point('other')]);e=copy.deepcopy(b);e['features'][0]['geometry']['coordinates'][2]+=1
        for case in ('conflicting_edit','conflicting_delete'):
            r=copy.deepcopy(b)
            if case=='conflicting_delete':r['features'].pop(0)
            else:r['features'][0]['properties']['matrix_external']='second-client-edit'
            self.assertTrue(m.final_oracle(case,b,e,r,r,'target',None))
            with self.assertRaises(m.p.existing.TestFailure):m.final_oracle(case,b,e,e,r,'target',None)

    def test_audit_requires_fault_and_rejects_duplicate_creates(self):
        w={'actor':'client','method':'PUT','path':m.p.FEATURES,'status':0,'outcome':'response_dropped_after_commit'}
        m.trial_audit('lost_create_response',[w])
        with self.assertRaises(m.p.existing.TestFailure):m.trial_audit('lost_create_response',[w,w])
        w={'actor':'client','method':'PATCH','path':m.p.FEATURES+'/target','status':503};ok=dict(w,status=200)
        m.trial_audit('server_error',[w,ok])
        for records in ([ok],[w],[w,ok,ok]):
            with self.assertRaises(m.p.existing.TestFailure):m.trial_audit('server_error',records)
        with self.assertRaises(m.p.existing.TestFailure):m.trial_audit('conflicting_edit',[ok])

    def test_real_http_faults_commit_lost_response_and_do_not_commit_503(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d)
            with socket.socket() as probe:probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
            server=m.Server(folder/'db.json',folder,port);m.p.write(server.db,state([point('target')]))
            def request(method,path,body):
                req=Request('http://127.0.0.1:'+str(port)+path,method=method,data=json.dumps(body).encode(),headers={'Content-Type':'application/json','User-Agent':'UnityPlayer/test'})
                with urlopen(req,timeout=3) as r:return json.load(r)
            try:
                server.start();m.p.write(folder/'fault.json',{'mode':'server_error','remaining':1})
                changed=point('target');changed['geometry']['coordinates'][2]=101
                with self.assertRaises(HTTPError) as error:request('PATCH',m.p.FEATURES+'/target',changed)
                self.assertEqual(error.exception.code,503);self.assertEqual(server.request(m.p.FEATURES)['features'][0],point('target'))
                request('PATCH',m.p.FEATURES+'/target',changed)
                self.assertEqual(server.request(m.p.FEATURES)['features'][0],changed)
                m.p.write(folder/'fault.json',{'mode':'lost_response','remaining':1})
                with self.assertRaises(Exception):request('PUT',m.p.FEATURES,point('new'))
                remote=server.request(m.p.FEATURES);self.assertEqual(len(remote['features']),2)
                self.assertTrue(next(f for f in remote['features'] if f['id']!='target')['id'].startswith('server_'))
                self.assertEqual(len(m.p.records(folder/'fault.json.jsonl')),2)
                self.assertTrue(any(x.get('outcome')=='response_dropped_after_commit' for x in m.p.records(server.audit)))
            finally:server.stop()

if __name__=='__main__':unittest.main()
