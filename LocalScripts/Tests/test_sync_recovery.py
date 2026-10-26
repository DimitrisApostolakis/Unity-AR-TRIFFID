"""Offline observer/oracle tests. Synthetic clients are not native results."""
import copy
import importlib.util
import json
from pathlib import Path
import socket
import tempfile
import threading
import time
import unittest
from urllib.request import Request,urlopen

spec=importlib.util.spec_from_file_location('recovery',Path(__file__).resolve().parents[1]/'run_sync_recovery.py')
recovery=importlib.util.module_from_spec(spec);spec.loader.exec_module(recovery)
pilot=recovery.pilot


def baseline():
    return {'features':[{'id':'target','type':'Feature','properties':{'id':'target','class':'fire','source':'robot'},
                         'geometry':{'type':'Point','coordinates':[23.7,38.,100.]}}],
            '_ar2Sync':{'schemaVersion':1,'nextRevision':1,'remoteIds':{'target':'target'},'pending':[],
                        'acknowledgedHashes':{'target':'base'}}}


def offline():
    old=baseline();root=copy.deepcopy(old);root['features'][0]['geometry']['coordinates'][2]=101.
    root['_ar2Sync']['nextRevision']=2
    root['_ar2Sync']['pending']=[{'kind':'update','featureId':'target','baseHash':'base','status':'pending','payload':copy.deepcopy(root['features'][0])}]
    return old,root


class RecoveryTests(unittest.TestCase):
    def test_pending_update_must_match_saved_feature_and_baseline(self):
        old,root=offline();self.assertIs(recovery.offline_edit(root,old,'target'),root)
        for key,value in [('kind','create'),('baseHash','wrong'),('status','conflict'),('payload',{})]:
            bad=copy.deepcopy(root);bad['_ar2Sync']['pending'][0][key]=value
            with self.assertRaises(recovery.Failure):recovery.offline_edit(bad,old,'target')

    def test_unchanged_point_waits_and_multiple_revisions_fail(self):
        self.assertFalse(recovery.offline_edit(baseline(),baseline(),'target'))
        old,root=offline();root['_ar2Sync']['nextRevision']=3
        with self.assertRaises(recovery.Failure):recovery.offline_edit(root,old,'target')

    def test_property_rewrites_and_changes_during_hold_fail(self):
        old,root=offline();frozen=copy.deepcopy(pilot.feature_map(root));root['features'][0]['geometry']['coordinates'][2]+=1
        root['_ar2Sync']['pending'][0]['payload']=copy.deepcopy(root['features'][0])
        with self.assertRaises(recovery.Failure):recovery.offline_edit(root,old,'target',frozen)
        old,root=offline();root['features'][0]['properties']['confidence']=None
        with self.assertRaises(recovery.Failure):recovery.offline_edit(root,old,'target')

    def test_guided_recovery_real_http_control_flow_with_synthetic_client(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);src=folder/'source';src.mkdir();prepared=folder/'prepared'
            pilot.write(src/'fixture.geojson',{'features':baseline()['features']})
            alignment={'origin_wgs84':{'lat':38,'lon':23.7,'alt':100},
                       'colmap_to_enu':{'scale':1,'R_rowmajor':[1,0,0,0,1,0,0,0,1],'t':[0,0,0]}}
            pilot.write(src/'alignment.json',alignment)
            pilot.write(src/'prepared_scene.json',{'files_sha256':{p.name:pilot.digest(p) for p in src.iterdir()}})
            pilot.write(src/'scene_runtime.json',{'completed':True,'source_state_restored':True})
            with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
            pilot.prepare(src,prepared,port)
            stop=threading.Event();errors=[]
            def actor():
                try:
                    done=set();root=None
                    def request(method,path,body=None):
                        req=Request(f'http://127.0.0.1:{port}'+path,method=method,
                            data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json','User-Agent':'UnityPlayer/SYNTHETIC-TEST'})
                        with urlopen(req,timeout=.5) as response:return json.load(response)
                    while not stop.is_set():
                        try:active=pilot.load(prepared/'active_run.json')
                        except (FileNotFoundError,json.JSONDecodeError):time.sleep(.05);continue
                        phase=active['phase']
                        if phase in ('finished','failed'):break
                        capture=prepared/'Captures'/active['capture']
                        cfg={'run_id':active['run_id'],'unity_version':'SYNTHETIC TEST ONLY','alignment':alignment,
                             'endpoints':{'GetFeaturesUrl':f'http://127.0.0.1:{port}'+pilot.FEATURES,
                             'GetStatusUrl':f'http://127.0.0.1:{port}'+pilot.STATUS,'GetLatestMqttUrl':f'http://127.0.0.1:{port}/wp-json/mqtt/v1/latest'},
                             'coordinator':{'enableRemotePolling':True,'remotePollingIntervalSeconds':.05,
                             'localScanIntervalSeconds':0,'updateDebounceSeconds':0,'localChangeGraceSeconds':0}}
                        def action(kind):
                            record={'run_id':active['run_id'],'kind':kind,'features_before':copy.deepcopy(root['features'])}
                            with (capture/'unity_actions.jsonl').open('a') as f:f.write(json.dumps(record)+'\n')
                        try:
                            if root is None:
                                root=request('GET',pilot.FEATURES);ids={str(f['id']):str(f['id']) for f in root['features']}
                                root['_ar2Sync']={'schemaVersion':1,'nextRevision':1,'remoteIds':ids,'acknowledgedHashes':{k:'base' for k in ids},'pending':[]}
                                root['lastModified']='baseline';pilot.write(prepared/'unity_state.json',root)
                            if phase=='awaiting_noop_save' and phase not in done:
                                pilot.write(capture/'unity_config_noop.json',cfg);action('unchanged_save_requested')
                                root['lastModified']='saved';pilot.write(prepared/'unity_state.json',root);done.add(phase)
                            if phase=='awaiting_edit' and phase not in done:
                                pilot.write(capture/'unity_config_edit.json',cfg);action('begin_point_edit');done.add(phase)
                            if phase=='offline_edit' and phase not in done:
                                root['features'][0]['geometry']['coordinates'][2]+=1
                                root['_ar2Sync']['nextRevision']=2
                                root['_ar2Sync']['pending']=[{'kind':'update','featureId':'target','baseHash':'base','status':'pending','payload':copy.deepcopy(root['features'][0])}]
                                root['lastModified']='offline saved';pilot.write(prepared/'unity_state.json',root);done.add(phase)
                            if phase=='recovering' and phase not in done:
                                request('PATCH',pilot.FEATURES+'/target',root['features'][0]);request('PATCH',pilot.STATUS,pilot.existing.AR_FLAGS)
                                root['_ar2Sync']['pending']=[];root['lastModified']='acknowledged';pilot.write(prepared/'unity_state.json',root);done.add(phase)
                            request('GET',pilot.STATUS)
                        except OSError:pass # Expected while the observer owns a real stopped server.
                        time.sleep(.1)
                except Exception as e:errors.append(e)
            thread=threading.Thread(target=actor,daemon=True);thread.start()
            try:
                capture=recovery.run(prepared,timeout=30)
                result=recovery.analyze(capture)
                self.assertEqual(result['confirmed_recovery_updates'],1)
                self.assertGreaterEqual(result['observed_convergence_after_health_seconds'],0)
                pilot.pack(capture)
                with self.assertRaises(recovery.Failure):recovery.run(prepared,timeout=1)
                (capture/'unity_offline.json').write_text('{}')
                with self.assertRaises(recovery.Failure):recovery.analyze(capture)
            finally:stop.set();thread.join(2)
            if errors:raise errors[0]


if __name__=='__main__':unittest.main()
