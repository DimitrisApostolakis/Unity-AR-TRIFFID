"""Pilot collector/oracle tests. These test fixtures are not native measurements."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import socket
import threading
import time
import tempfile
import unittest
from urllib.request import Request, urlopen

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('sync_pilot_runner',ROOT/'LocalScripts/run_sync_pilot.py')
runner=importlib.util.module_from_spec(spec);spec.loader.exec_module(runner)


def point(fid):
    return {'type':'Feature','id':fid,'properties':{'id':fid,'class':'animal','source':'Robot-A'},
            'geometry':{'type':'Point','coordinates':[23.7,37.9,100.]}}


def baseline():
    foreign=point(runner.FOREIGN_ID);foreign['properties']['class']='Unsupported_Class'
    foreign['properties']['extra']={'KeepCase':[1,None,True]}
    return {'features':[point('target'),foreign],'lastModified':'before','_ar2Sync':{
        'schemaVersion':1,'pending':[],'remoteIds':{'target':'target',runner.FOREIGN_ID:runner.FOREIGN_ID},'nextRevision':1}}


def record(method='GET',path=runner.FEATURES,body=None,status=200):
    return {'method':method,'path':path,'body':body,'status':status,'user_agent':'UnityPlayer/test','actor':'client','time_ns':1}


def edit_case():
    old=baseline();local=copy.deepcopy(old);local['features'][0]['geometry']['coordinates'][2]=101.
    local['_ar2Sync']['nextRevision']=2
    remote=copy.deepcopy(local)
    return old,local,remote,[record('PATCH',runner.FEATURES+'/target',copy.deepcopy(local['features'][0]))]


class PilotTests(unittest.TestCase):
    def test_prepare_preserves_input_and_creates_isolated_foreign_probe(self):
        with tempfile.TemporaryDirectory() as temp:
            source=Path(temp)/'source';source.mkdir();output=Path(temp)/'pilot'
            runner.write(source/'fixture.geojson',{'type':'FeatureCollection','features':[point('target')]})
            runner.write(source/'alignment.json',{'fixture':'unit-test only'})
            runner.write(source/'plan.json',{'fixture':'unit-test only'})
            runner.write(source/'prepared_scene.json',{'files_sha256':{p.name:runner.digest(p) for p in source.iterdir()}})
            runner.write(source/'scene_runtime.json',{'completed':True,'source_state_restored':True})
            before={p.name:p.read_bytes() for p in source.iterdir()}
            runner.prepare(source,output,8080)
            self.assertEqual(before,{p.name:p.read_bytes() for p in source.iterdir()})
            seed=runner.load(output/'server_seed.json');self.assertEqual(len(seed['features']),2)
            self.assertEqual(seed['features'][1]['id'],runner.FOREIGN_ID)
            self.assertFalse((output/'unity_state.json').exists())
            with self.assertRaises(FileExistsError):runner.prepare(source,output,8080)
            (source/'fixture.geojson').write_text('{}')
            with self.assertRaises(runner.existing.TestFailure):runner.prepare(source,Path(temp)/'bad',8080)

    def test_noop_requires_exact_feature_semantics_and_no_operations(self):
        old=baseline();root=copy.deepcopy(old);root['lastModified']='new-save'
        runner.require_noop(root,copy.deepcopy(root),old,[record()])
        for mutate in [lambda x:x['features'][1]['properties'].update(extra={'changed':True}),
                       lambda x:x['_ar2Sync'].update(nextRevision=2),
                       lambda x:x['_ar2Sync']['pending'].append({'featureId':'target'})]:
            changed=copy.deepcopy(root);mutate(changed)
            with self.assertRaises(runner.existing.TestFailure):runner.require_noop(changed,root,old,[])

    def test_failed_write_attempt_is_still_a_noop_failure(self):
        for method in ['PUT','POST','PATCH','DELETE']:
            with self.assertRaises(runner.existing.TestFailure):
                runner.require_noop(baseline(),baseline(),baseline(),[record(method,runner.FEATURES+'/target',status=503)])

    def test_sync_read_evidence_excludes_telemetry_harness_and_failed_requests(self):
        requests=[record(),record(path=runner.STATUS),record(path='/wp-json/mqtt/v1/latest'),
                  dict(record(),actor='harness'),record(status=500),dict(record(),user_agent='browser')]
        self.assertEqual(len(runner.sync_reads(requests)),2)

    def test_one_saved_edit_requires_exact_request_server_and_local_agreement(self):
        old,local,remote,audit=edit_case()
        self.assertTrue(runner.require_edit(local,remote,old,'target',audit))
        self.assertFalse(runner.require_edit(local,old,old,'target',[]))
        local['_ar2Sync']['pending']=[{'featureId':'target'}]
        self.assertFalse(runner.require_edit(local,remote,old,'target',audit))

    def test_duplicate_updates_and_other_actor_writes_fail(self):
        old,local,remote,audit=edit_case()
        for bad in [audit+audit,[dict(audit[0],method='PUT')],[dict(audit[0],user_agent='browser')]]:
            with self.assertRaises(runner.existing.TestFailure):runner.require_edit(local,remote,old,'target',bad)

    def test_unsupported_features_and_target_properties_must_be_preserved(self):
        for key in ['foreign','target']:
            old,local,remote,audit=edit_case()
            local['features'][1 if key=='foreign' else 0]['properties']['source']='rewritten'
            with self.assertRaises(runner.existing.TestFailure):runner.require_edit(local,remote,old,'target',audit)

    def test_nonfinite_or_wrong_geometry_cannot_pass_delivery(self):
        for coords in [[23,38,float('nan')],[],[23,38]]:
            old,local,remote,audit=edit_case();local['features'][0]['geometry']['coordinates']=coords
            with self.assertRaises(runner.existing.TestFailure):runner.require_edit(local,remote,old,'target',audit)

    def test_foreign_data_cannot_be_hidden_by_duplicate_ids(self):
        root=baseline();root['features'].append(copy.deepcopy(root['features'][0]))
        with self.assertRaises(runner.existing.TestFailure):runner.feature_map(root)

    def test_observation_covers_two_actual_polling_intervals(self):
        cfg={'coordinator':{'remotePollingIntervalSeconds':30,'localChangeGraceSeconds':1.5,
                           'updateDebounceSeconds':.5,'localScanIntervalSeconds':.25}}
        self.assertEqual(runner.observation_seconds(cfg,5),63.25)
        self.assertEqual(runner.observation_seconds(cfg,90),90)

    def test_jsonl_does_not_silently_drop_corrupt_middle_records(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'events.jsonl';path.write_text('{}\n{"unfinished":')
            self.assertEqual(runner.records(path),[{}])
            with self.assertRaises(runner.existing.TestFailure):runner.records(path,partial=False)
            path.write_text('{}\nnot-json\n{}\n')
            with self.assertRaises(runner.existing.TestFailure):runner.records(path)

    def test_real_local_http_audit_distinguishes_reads_and_one_write(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);db=folder/'db.json';runner.write(db,baseline())
            with socket.socket() as probe:probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
            server=runner.existing.LocalServer(db,folder,port)
            def request(method,path,body=None):
                req=Request(f'http://127.0.0.1:{port}'+path,method=method,
                    data=None if body is None else json.dumps(body).encode(),
                    headers={'Content-Type':'application/json','User-Agent':'UnityPlayer/test'})
                with urlopen(req,timeout=2) as response:return json.load(response)
            try:
                server.start();request('GET',runner.FEATURES);request('GET',runner.STATUS)
                old=baseline();audit=runner.records(server.audit);runner.require_noop(old,server.request(runner.FEATURES),old,audit)
                self.assertEqual(len(runner.sync_reads(audit)),2)
                changed=point('target');changed['geometry']['coordinates'][2]=101.
                request('PATCH',runner.FEATURES+'/target',changed)
                records=runner.records(server.audit);self.assertEqual(len(runner.feature_writes(records)),1)
                self.assertEqual(runner.load(db)['features'][0],changed)
            finally:server.stop()

    def test_guided_control_flow_and_analysis_with_explicit_synthetic_client(self):
        # Exercises the collector, real HTTP audit and evidence analysis. This
        # synthetic actor is a unit-test double, never native experiment evidence.
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);source=folder/'source';source.mkdir();pilot=folder/'pilot'
            runner.write(source/'fixture.geojson',{'type':'FeatureCollection','features':[point('target')]})
            alignment={'origin_wgs84':{'lon':23.7,'lat':37.9,'alt':100},
                       'colmap_to_enu':{'scale':1,'R_rowmajor':[1,0,0,0,1,0,0,0,1],'t':[0,0,0]}}
            runner.write(source/'alignment.json',alignment)
            runner.write(source/'prepared_scene.json',{'files_sha256':{p.name:runner.digest(p) for p in source.iterdir()}})
            runner.write(source/'scene_runtime.json',{'completed':True,'source_state_restored':True})
            with socket.socket() as probe:probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
            runner.prepare(source,pilot,port)
            stop=threading.Event();errors=[]
            def synthetic_actor():
                try:
                    handled=set();root=None
                    def request(method,path,body=None):
                        req=Request(f'http://127.0.0.1:{port}'+path,method=method,
                            data=None if body is None else json.dumps(body).encode(),
                            headers={'Content-Type':'application/json','User-Agent':'UnityPlayer/unit-test-double'})
                        with urlopen(req,timeout=2) as response:return json.load(response)
                    while not stop.is_set():
                        try:active=runner.load(pilot/'active_run.json')
                        except (FileNotFoundError,json.JSONDecodeError):time.sleep(.05);continue
                        phase=active['phase']
                        if phase in ['finished','failed']:break
                        capture=pilot/'Captures'/active['capture']
                        cfg={'run_id':active['run_id'],'unity_version':'synthetic unit test, not native Unity',
                             'alignment':alignment,'endpoints':{'GetFeaturesUrl':f'http://127.0.0.1:{port}'+runner.FEATURES,
                             'GetStatusUrl':f'http://127.0.0.1:{port}'+runner.STATUS,
                             'GetLatestMqttUrl':f'http://127.0.0.1:{port}/wp-json/mqtt/v1/latest'},
                             'coordinator':{'enableRemotePolling':True,'remotePollingIntervalSeconds':.05,
                             'localScanIntervalSeconds':0,'updateDebounceSeconds':0,'localChangeGraceSeconds':0}}
                        def action(kind):
                            event={'run_id':active['run_id'],'kind':kind,'features_before':copy.deepcopy(root['features'])}
                            with (capture/'unity_actions.jsonl').open('a') as f:f.write(json.dumps(event)+'\n')
                        if 'initial' not in handled:
                            root=request('GET',runner.FEATURES)
                            root['_ar2Sync']={'schemaVersion':1,'nextRevision':1,'pending':[],
                                'remoteIds':{str(f['id']):str(f['id']) for f in root['features']}}
                            root['lastModified']='initial';runner.write(pilot/'unity_state.json',root);handled.add('initial')
                        if phase=='awaiting_noop_save' and phase not in handled:
                            runner.write(capture/'unity_config_noop.json',cfg);action('unchanged_save_requested')
                            root['lastModified']='saved';runner.write(pilot/'unity_state.json',root);handled.add(phase)
                        if phase=='awaiting_edit' and phase not in handled:
                            runner.write(capture/'unity_config_edit.json',cfg);action('begin_point_edit')
                            root['features'][0]['geometry']['coordinates'][2]=101.
                            request('PATCH',runner.FEATURES+'/target',root['features'][0])
                            request('PATCH',runner.STATUS,runner.existing.AR_FLAGS)
                            root['_ar2Sync']['nextRevision']=2;root['lastModified']='edit saved'
                            runner.write(pilot/'unity_state.json',root);handled.add(phase)
                        request('GET',runner.STATUS)
                        time.sleep(.25)
                except Exception as error:errors.append(error)
            thread=threading.Thread(target=synthetic_actor,daemon=True);thread.start()
            try:
                capture=runner.run(pilot,timeout=15,minimum=5,quiet=.05)
                self.assertEqual(runner.analyze(capture)['result'],'PASSED')
                archive=runner.pack(capture);self.assertTrue(archive.is_file())
                with self.assertRaises(FileExistsError):runner.pack(capture)
                path=capture/'unity_noop.json';path.write_text('{}')
                with self.assertRaises(runner.existing.TestFailure):runner.analyze(capture)
            finally:stop.set();thread.join(3)
            if errors:raise errors[0]


if __name__=='__main__':unittest.main()
