#!/usr/bin/env python3
"""Guided real-Unity online no-op + one-point-edit pilot; Python 3.9+, stdlib only.

Owns a private local server database. Never writes Unity's snapshot, fabricates
acknowledgements, or simulates the application. This is one correctness pilot,
not the complete two-client/fault reliability matrix or a latency benchmark.
"""
from __future__ import annotations
import argparse
import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import shutil
import struct
import sys
import time
import uuid
import zipfile
from urllib.error import URLError

REPO = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location('_ar2_offline_observer', REPO/'LocalScripts/test_ar2_offline_sync.py')
existing = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(existing)
ASSETS = REPO / 'projects/TRIFFID/Assets'
FEATURES, STATUS = existing.FEATURES, existing.STATUS
FOREIGN_ID = 'FOREIGN_Probe_KeepCase'
WRITES = {'POST', 'PUT', 'PATCH', 'DELETE'}


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_hashes():
    paths = list((ASSETS/'TRIFFID/Runtime').rglob('*.cs'))
    paths += [ASSETS/'TRIFFID/Editor/Experiments/SyncPilotActions.cs',
              REPO/'LocalScripts/local_geojson_server.py', Path(__file__),
              REPO/'LocalScripts/test_ar2_offline_sync.py']
    return {str(p.relative_to(REPO)).replace('\\','/'): hashlib.sha256(
        (p.read_text(encoding='utf-8-sig').replace('\r\n','\n').rstrip('\r\n')+'\n').encode()).hexdigest()
        for p in sorted(paths)}


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    path = Path(path)
    temporary = path.with_name(path.name+'.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    for attempt in range(20):
        try:
            temporary.replace(path)
            return
        except PermissionError:
            if attempt == 19: raise
            time.sleep(.05)


def feature_map(root):
    features = root.get('features')
    if not isinstance(features, list): raise existing.TestFailure('Missing complete features array')
    result = {}
    for feature in features:
        if not isinstance(feature, dict) or feature.get('id') is None:
            raise existing.TestFailure('Feature without an identifier')
        key = str(feature['id'])
        if key in result: raise existing.TestFailure('Duplicate feature ID: '+key)
        result[key] = feature
    return result


def settled(root, expected):
    state = existing.outbox(root)
    if state['pending']: return False
    if feature_map(root) != expected: return False
    return all(str(state['remoteIds'].get(key)) == key for key in expected)


def records(path, partial=True):
    if not Path(path).exists(): return []
    lines = Path(path).read_text(encoding='utf-8-sig').splitlines()
    result = []
    for index, line in enumerate(lines):
        if not line.strip(): continue
        try: result.append(json.loads(line))
        except json.JSONDecodeError:
            if partial and index == len(lines)-1: break
            raise existing.TestFailure('Malformed evidence record: '+str(path))
    return result


def feature_writes(audit):
    return [r for r in audit if r.get('actor') != 'harness' and
            (r.get('path') == FEATURES or r.get('path','').startswith(FEATURES+'/')) and r.get('method') in WRITES]


def sync_reads(audit):
    return [r for r in audit if r.get('actor') != 'harness' and
            r.get('user_agent','').startswith('UnityPlayer') and r.get('method') == 'GET' and
            r.get('path') in (FEATURES,STATUS) and 200 <= r.get('status',0) < 300]


def require_noop(root, remote, baseline, audit):
    if feature_map(root) != feature_map(baseline) or feature_map(remote) != feature_map(baseline):
        raise existing.TestFailure('An unedited save changed local/server feature data')
    state, old = existing.outbox(root), existing.outbox(baseline)
    if state['pending'] or state['nextRevision'] != old['nextRevision']:
        raise existing.TestFailure('An unedited save created outbox work')
    if feature_writes(audit): raise existing.TestFailure('An unedited save attempted an HTTP feature write')


def require_edit(root, remote, baseline, target, audit):
    before, local, server = feature_map(baseline), feature_map(root), feature_map(remote)
    if set(local) != set(before) or set(server) != set(before):
        raise existing.TestFailure('The single-edit trial created/deleted feature IDs')
    if any(local[key] != before[key] or server[key] != before[key] for key in before if key != target):
        raise existing.TestFailure('The single edit changed an unrelated feature, including the foreign-class probe')
    for value in [local[target], server[target]]:
        geometry = value.get('geometry') or {}
        coordinates = geometry.get('coordinates')
        if geometry.get('type') != 'Point' or not isinstance(coordinates,list) or len(coordinates) != 3 or any(
            not isinstance(v,(int,float)) or isinstance(v,bool) or not math.isfinite(v) for v in coordinates):
            raise existing.TestFailure('The target must retain finite point coordinates')
        allowed = {'altitude_m','height_above_surface_m','heightSource','heightStatus','elevationStatus'}
        protected = lambda feature:{k:v for k,v in (feature.get('properties') or {}).items() if k not in allowed}
        if protected(value) != protected(before[target]):
            raise existing.TestFailure('The position edit changed an unrelated target property')
    writes = feature_writes(audit)
    if any(r.get('method') != 'PATCH' or r.get('path') != FEATURES+'/'+target or
           not r.get('user_agent','').startswith('UnityPlayer') for r in writes):
        raise existing.TestFailure('Unexpected feature write during the single-point edit')
    if len(writes) > 1: raise existing.TestFailure('More than one PATCH for one ordinary online edit')
    if not writes or not 200 <= writes[0].get('status',0) < 300: return False
    if local[target].get('geometry') == before[target].get('geometry'): return False
    if local[target] != server[target] or writes[0].get('body') != local[target]: return False
    state = existing.outbox(root)
    return not state['pending'] and state['nextRevision'] == existing.outbox(baseline)['nextRevision']+1


def configuration(capture, kind, run_id, port):
    value = load(capture/('unity_config_'+kind+'.json'))
    if value.get('run_id') != run_id: raise existing.TestFailure('Stale Unity configuration export')
    for name,path in [('GetFeaturesUrl',FEATURES),('GetStatusUrl',STATUS),('GetLatestMqttUrl','/wp-json/mqtt/v1/latest')]:
        if value['endpoints'].get(name) != f'http://127.0.0.1:{port}'+path:
            raise existing.TestFailure('Unity is not using the isolated pilot endpoints')
    cfg = value['coordinator']
    def calibration(config):
        transform = config['colmap_to_enu']
        f32 = lambda v:struct.unpack('f',struct.pack('f',v))[0]
        return (config['origin_wgs84'], f32(transform['scale']),
                [f32(v) for v in transform['R_rowmajor']], [f32(v) for v in transform['t']])
    if calibration(value['alignment']) != calibration(load(capture/'alignment.json')):
        raise existing.TestFailure('The loaded Unity scene has a different geographic alignment')
    if cfg.get('enableRemotePolling') is not True: raise existing.TestFailure('Remote polling is disabled')
    for name in ['remotePollingIntervalSeconds','localScanIntervalSeconds','updateDebounceSeconds','localChangeGraceSeconds']:
        v = cfg.get(name)
        if not isinstance(v,(int,float)) or isinstance(v,bool) or not math.isfinite(v) or v < 0:
            raise existing.TestFailure('Invalid exported timing: '+name)
    return value


def observation_seconds(cfg, minimum):
    c = cfg['coordinator']
    return max(minimum, 2*max(2,c['remotePollingIntervalSeconds']) +
               c['localChangeGraceSeconds'] + c['updateDebounceSeconds'] + c['localScanIntervalSeconds'] + 1)


def prepare(source, output, port):
    if not 0 < port < 65536: raise ValueError('Invalid port')
    manifest, runtime = load(source/'prepared_scene.json'), load(source/'scene_runtime.json')
    if not runtime.get('completed') or not runtime.get('source_state_restored'):
        raise existing.TestFailure('Use a completed scene capture with restored source state')
    for name,h in manifest['files_sha256'].items():
        if digest(source/name) != h: raise existing.TestFailure('Changed input: '+name)
    seed = {'type':'FeatureCollection','features':copy.deepcopy(load(source/'fixture.geojson')['features'])}
    target = next(f for f in seed['features'] if f.get('geometry',{}).get('type') == 'Point')
    if FOREIGN_ID in feature_map(seed): raise existing.TestFailure('Foreign probe ID collision')
    seed['features'].append({'type':'Feature','id':FOREIGN_ID,'vendor':{'Case':'Kept','values':[True,None,7]},
        'properties':{'id':'Property_ID_KeepCase','class':'__AR2_Unsupported_Probe__','source':'Mission-A',
                      'confidence':.12345678912345,'extra':{'nested':['A','B']},'marker-color':'#AaBbCc'},
        'geometry':{'type':'Point','coordinates':copy.deepcopy(target['geometry']['coordinates']),'vendor_geometry':True}})
    seed['observer_status'] = {'fe_updated':1,'mobile_updated':0,'ar_updated':0}
    output.mkdir(parents=True,exist_ok=False)
    write(output/'server_seed.json',seed)
    shutil.copyfile(source/'alignment.json',output/'alignment.json')
    value = {'schema_version':1,'prepared_id':uuid.uuid4().hex,'port':port,'target_id':str(target['id']),
        'created_utc':datetime.now(timezone.utc).isoformat(),'source_capture':source.name,
        'source_capture_runtime_sha256':digest(source/'scene_runtime.json'),
        'files_sha256':{n:digest(output/n) for n in ['server_seed.json','alignment.json']},
        'sources_lf_sha256':source_hashes(),
        'scope':'One guided normal-online unchanged-save and one-point-edit correctness pilot; real Unity and local HTTP. Not the two-client fault matrix or a latency benchmark.'}
    write(output/'sync_pilot.json',value)
    print('Prepared:',output,flush=True)


def wait_until(label, check, timeout, quiet=0):
    deadline, good_since = time.monotonic()+timeout, None
    while time.monotonic() < deadline:
        try: result = check()
        except (FileNotFoundError,json.JSONDecodeError,PermissionError): result = False
        now = time.monotonic()
        if result:
            good_since = now if good_since is None else good_since
            if now-good_since >= quiet: return result
        else: good_since = None
        time.sleep(.25)
    raise existing.TestFailure('Timed out: '+label)


def run(folder, timeout=300, minimum=5, quiet=3):
    prepared = load(folder/'sync_pilot.json')
    if prepared.get('schema_version') != 1: raise existing.TestFailure('Unsupported pilot input schema')
    for name,h in prepared['files_sha256'].items():
        if digest(folder/name) != h: raise existing.TestFailure('Pilot input changed: '+name)
    if prepared['sources_lf_sha256'] != source_hashes():
        raise existing.TestFailure('Code differs from preparation. Pull the matching branch or prepare a new input folder.')
    snapshot = folder/'unity_state.json'
    if snapshot.exists() or (folder/'active_run.json').exists():
        raise existing.TestFailure('This folder was already used. Preserve its evidence and prepare a NEW pilot folder.')
    capture = folder/'Captures'/('Capture_'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'_'+uuid.uuid4().hex[:8])
    capture.mkdir(parents=True)
    shutil.copyfile(folder/'server_seed.json',capture/'server_db.json')
    shutil.copyfile(folder/'server_seed.json',capture/'server_seed.json')
    shutil.copyfile(folder/'sync_pilot.json',capture/'sync_pilot.json')
    shutil.copyfile(folder/'alignment.json',capture/'alignment.json')
    run_id = uuid.uuid4().hex
    report = {'schema_version':1,'run_id':run_id,'completed':False,'result':'FAILED',
              'scope':prepared['scope'],'started_utc':datetime.now(timezone.utc).isoformat(),
              'not_measured':['actual Unity acknowledgement latency','marker-visible timing','remote production backend',
                              'two-client conflicts','fault/reconnection/restart scenarios']}
    server = existing.LocalServer(capture/'server_db.json',capture,prepared['port'])
    def phase(name):
        write(folder/'active_run.json',{'prepared_id':prepared['prepared_id'],'run_id':run_id,'capture':capture.name,'phase':name})
    def snapshots(label):
        local,remote = load(snapshot),server.request(FEATURES)
        write(capture/('unity_'+label+'.json'),local);write(capture/('server_'+label+'.json'),remote)
        return local,remote
    try:
        server.start();phase('starting')
        seed = feature_map(load(folder/'server_seed.json'))
        print('\nServer ready. Keep the normal Unity scene RUNNING and UNPAUSED.',flush=True)
        print('Before Play mode: enable API Use Localhost; set JsonSpawner Save File Path to:\n'+str(snapshot),flush=True)
        print('Keep the normal source GeoJSON path and alignment. Finish/cancel drawings and release markers.',flush=True)
        print('Waiting for the private fixture to be synchronized into the saved Unity file...',flush=True)
        wait_until('initial fixture load',lambda:settled(load(snapshot),seed),timeout,quiet)
        baseline,_ = snapshots('baseline')
        startup_writes = feature_writes(records(server.audit))
        report['startup_feature_write_attempts'] = len(startup_writes)
        if startup_writes: raise existing.TestFailure('Unity attempted feature writes while loading the unchanged fixture')
        baseline_revision = existing.outbox(baseline)['nextRevision']
        before_noop = len(records(server.audit))
        report['baseline_revision'] = baseline_revision
        phase('awaiting_noop_save')
        print('\nSTEP 1: Select JsonSpawner. Tools > Experiments > Synchronization > Record Unchanged Save.',flush=True)
        print('Do not edit any feature. The helper records the actual settings and invokes the normal save.',flush=True)
        action_file = capture/'unity_actions.jsonl'
        def save_detected():
            actions = records(action_file)
            if not actions: return False
            if len(actions) != 1 or actions[0].get('kind') != 'unchanged_save_requested' or actions[0].get('run_id') != run_id:
                raise existing.TestFailure('Unexpected Unity action evidence')
            if feature_map({'features':actions[0]['features_before']}) != feature_map(baseline):
                raise existing.TestFailure('Unity feature data changed before the unchanged save')
            cfg = configuration(capture,'noop',run_id,prepared['port'])
            root = load(snapshot)
            require_noop(root,server.request(FEATURES),baseline,records(server.audit)[before_noop:])
            return cfg if root.get('lastModified') and root.get('lastModified') != baseline.get('lastModified') else False
        cfg = wait_until('a durable unchanged Unity save',save_detected,timeout)
        duration = observation_seconds(cfg,minimum)
        phase('observing_noop');print(f'Observing unchanged state for {duration:.1f} seconds (covers two configured polling intervals)...',flush=True)
        started = time.monotonic()
        observing_start = len(records(server.audit))
        while time.monotonic()-started < duration:
            require_noop(load(snapshot),server.request(FEATURES),baseline,records(server.audit)[before_noop:]);time.sleep(.25)
        local,remote = snapshots('noop')
        audit_noop = records(server.audit)[before_noop:]
        observed_reads = len(sync_reads(records(server.audit)[observing_start:]))
        if observed_reads < 2: raise existing.TestFailure('Fewer than two real Unity feature/status reads during observation; active synchronization was not demonstrated')
        report['noop'] = {'passed':True,'observation_seconds':time.monotonic()-started,'new_outbox_operations':0,
            'feature_write_attempts':0,'unity_sync_reads':len(sync_reads(audit_noop)),'durable_save_observed':True,
            'audit_start':before_noop,'observation_audit_start':observing_start,
            'observation_unity_sync_reads':observed_reads,'audit_end':len(records(server.audit))}
        phase('awaiting_edit');before_edit = report['noop']['audit_end']
        print('\nSTEP 2: Tools > Experiments > Synchronization > Begin Point Edit.',flush=True)
        print('Then move ONLY point ID '+prepared['target_id']+' once in Unity and RELEASE it. Make no other edits.',flush=True)
        def edit_converged():
            actions = records(action_file)
            if len(actions) < 2: return False
            if len(actions) != 2 or actions[1].get('kind') != 'begin_point_edit' or actions[1].get('run_id') != run_id:
                raise existing.TestFailure('Unexpected edit action evidence')
            edit_cfg = configuration(capture,'edit',run_id,prepared['port'])
            if edit_cfg != cfg: raise existing.TestFailure('Unity settings changed between the no-op and edit trials')
            if feature_map({'features':actions[1]['features_before']}) != seed:
                raise existing.TestFailure('Feature data changed before the edit was marked')
            return require_edit(load(snapshot),server.request(FEATURES),local,prepared['target_id'],records(server.audit)[before_edit:])
        confirmation = observation_seconds(cfg,quiet)
        print(f'After delivery, the unchanged final state must remain stable for {confirmation:.1f} seconds.',flush=True)
        wait_until('one edit delivered and acknowledged in the durable outbox',edit_converged,timeout,confirmation)
        local_after,remote_after = snapshots('edit')
        after_audit = records(server.audit)[before_edit:]
        if existing.observer_problems(after_audit,0):
            raise existing.TestFailure('; '.join(existing.observer_problems(after_audit,0)))
        report['edit'] = {'passed':True,'target_id':prepared['target_id'],'feature_write_attempts':1,
            'confirmed_updates':1,'unrelated_features_preserved':True,'foreign_class_preserved':True,
            'outbox_empty':True,'quiet_confirmation_seconds':confirmation,'audit_start':before_edit,
            'audit_end':len(records(server.audit)),'delivery_latency':'not measured by this pilot'}
        report.update(completed=True,result='PASSED')
        print('\nPASSED. Stop Unity Play mode, then restore the original Save File Path and API environment.',flush=True)
        print('Capture: '+str(capture),flush=True)
        return capture
    except (Exception,KeyboardInterrupt) as error:
        report['error'] = str(error) or type(error).__name__
        print('FAILED: '+report['error'],file=sys.stderr,flush=True)
        raise
    finally:
        server.stop();phase('finished' if report['completed'] else 'failed')
        if snapshot.exists(): shutil.copyfile(snapshot,capture/'unity_final.json')
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        report['files_sha256'] = {f.name:digest(f) for f in capture.iterdir() if f.is_file() and f.name!='report.json'}
        write(capture/'report.json',report)


def analyze(capture):
    report = load(capture/'report.json')
    if not report.get('completed') or report.get('result') != 'PASSED': raise existing.TestFailure('Incomplete/failed pilot; retain as failure evidence')
    for name,h in report['files_sha256'].items():
        if digest(capture/name) != h: raise existing.TestFailure('Evidence hash mismatch: '+name)
    baseline,local,remote = load(capture/'unity_baseline.json'),load(capture/'unity_noop.json'),load(capture/'server_noop.json')
    seed = feature_map(load(capture/'server_seed.json'))
    if not settled(baseline,seed) or feature_map(load(capture/'server_baseline.json')) != seed:
        raise existing.TestFailure('The initial acknowledged state differs from the declared private fixture')
    audit = records(capture/'requests.jsonl',partial=False)
    noop,edit = report['noop'],report['edit']
    bounds = [noop['audit_start'],noop['observation_audit_start'],noop['audit_end'],edit['audit_start'],edit['audit_end']]
    if any(not isinstance(i,int) or isinstance(i,bool) or i < 0 or i > len(audit) for i in bounds) or bounds != sorted(bounds):
        raise existing.TestFailure('Invalid audit phase boundaries')
    if feature_writes(audit[:noop['audit_start']]): raise existing.TestFailure('Unexpected startup feature writes')
    if len(feature_writes(audit)) != 1: raise existing.TestFailure('Unexpected feature writes outside the declared edit')
    no_records = audit[noop['audit_start']:noop['audit_end']]
    require_noop(local,remote,baseline,no_records)
    if local.get('lastModified') == baseline.get('lastModified') or len(sync_reads(audit[noop['observation_audit_start']:noop['audit_end']])) < 2:
        raise existing.TestFailure('No durable save or active synchronization evidence')
    prepared = load(capture/'sync_pilot.json')
    actions = records(capture/'unity_actions.jsonl',partial=False)
    if len(actions) != 2 or [a.get('kind') for a in actions] != ['unchanged_save_requested','begin_point_edit'] or any(
        a.get('run_id') != report['run_id'] or feature_map({'features':a.get('features_before')}) != feature_map(baseline) for a in actions):
        raise existing.TestFailure('Missing or inconsistent native Unity action markers')
    if edit['target_id'] != prepared['target_id']: raise existing.TestFailure('Edit target differs from the declared fixture')
    cfg = configuration(capture,'noop',report['run_id'],prepared['port'])
    if configuration(capture,'edit',report['run_id'],prepared['port']) != cfg:
        raise existing.TestFailure('Settings changed between phases')
    if noop['observation_seconds'] < observation_seconds(cfg,5):
        raise existing.TestFailure('Observation is shorter than two configured polling intervals')
    if edit['quiet_confirmation_seconds'] < observation_seconds(cfg,3):
        raise existing.TestFailure('Edit confirmation window is too short')
    edit_records = audit[edit['audit_start']:edit['audit_end']]
    if not require_edit(load(capture/'unity_edit.json'),load(capture/'server_edit.json'),local,edit['target_id'],edit_records):
        raise existing.TestFailure('Edit delivery evidence does not match its oracle')
    problems = existing.observer_problems(edit_records,0)
    if problems: raise existing.TestFailure('; '.join(problems))
    if feature_map(load(capture/'server_db.json')) != feature_map(load(capture/'server_edit.json')):
        raise existing.TestFailure('Server disk state differs from the final observed state')
    print(json.dumps({'result':'PASSED','noop_feature_writes':0,'confirmed_point_updates':1,
                      'foreign_class_preserved':True,'delivery_latency':'not measured'},indent=2))
    return report


def pack(capture):
    report = load(capture/'report.json')
    for name,h in report['files_sha256'].items():
        if digest(capture/name) != h: raise existing.TestFailure('Evidence hash mismatch: '+name)
    archive = capture.with_suffix('.zip')
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(capture.iterdir()):
            if p.is_file(): z.write(p,Path(capture.name)/p.name)
    print('Archive:',archive)
    return archive


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode=parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--prepare-from-capture',type=Path);mode.add_argument('--run',type=Path)
    mode.add_argument('--analyze-run',type=Path);mode.add_argument('--pack-run',type=Path)
    parser.add_argument('--output',type=Path);parser.add_argument('--port',type=int,default=8080)
    parser.add_argument('--timeout',type=float,default=300);parser.add_argument('--minimum-observe-seconds',type=float,default=5)
    parser.add_argument('--quiet-seconds',type=float,default=3)
    a=parser.parse_args(argv)
    try:
        if a.prepare_from_capture:
            if not a.output: parser.error('--output NEW_FOLDER is required')
            prepare(a.prepare_from_capture.resolve(),a.output.resolve(),a.port)
        elif a.run:
            if any(not math.isfinite(v) or v <= 0 for v in [a.timeout,a.minimum_observe_seconds,a.quiet_seconds]):
                parser.error('Timing values must be finite and positive')
            capture=run(a.run.resolve(),a.timeout,a.minimum_observe_seconds,a.quiet_seconds)
            pack(capture)
        elif a.analyze_run: analyze(a.analyze_run.resolve())
        else: pack(a.pack_run.resolve())
        return 0
    except KeyboardInterrupt: return 130
    except (OSError,ValueError,KeyError,existing.TestFailure,URLError) as error:
        print('FAILED: '+str(error),file=sys.stderr);return 1


if __name__=='__main__': raise SystemExit(main())
