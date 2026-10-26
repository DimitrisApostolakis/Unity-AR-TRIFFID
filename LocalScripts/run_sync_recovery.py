#!/usr/bin/env python3
"""Native Unity offline point-edit/recovery pilot, private local HTTP only.

Reuse a NEW extracted PreparedSyncPilot input folder. Never writes Unity state.
This measures an outage/recovery, not response loss, two-client races or a crash.
"""
from __future__ import annotations
import argparse
import copy
from datetime import datetime,timezone
import importlib.util
from pathlib import Path
import shutil
import sys
import time
import uuid

spec=importlib.util.spec_from_file_location('recovery_pilot',Path(__file__).with_name('run_sync_pilot.py'))
pilot=importlib.util.module_from_spec(spec);spec.loader.exec_module(pilot)
Failure=pilot.existing.TestFailure


def offline_edit(root,baseline,target,previous=None):
    # Reuse strict point/property/unrelated-feature validation, without claiming
    # that passing this validation delivers a network operation.
    pilot.require_edit(root,root,baseline,target,[])
    local,old=pilot.feature_map(root),pilot.feature_map(baseline)
    if local[target]['geometry']==old[target]['geometry']:return False
    state,initial=pilot.existing.outbox(root),pilot.existing.outbox(baseline)
    if state['nextRevision']!=initial['nextRevision']+1:raise Failure('Expected exactly one offline edit revision')
    pending=state['pending']
    if len(pending)!=1 or pending[0].get('kind')!='update' or str(pending[0].get('featureId'))!=target:
        raise Failure('Offline edit is not one durable pending target update')
    if pending[0].get('payload')!=local[target]:raise Failure('Pending offline payload differs from saved feature')
    if pending[0].get('baseHash')!=initial['acknowledgedHashes'].get(target):raise Failure('Offline edit lost its acknowledged baseline')
    if pending[0].get('status') in ('conflict','reconcile'):raise Failure('Ordinary offline update entered conflict/reconciliation')
    if previous is not None and local!=previous:raise Failure('Feature data changed during the offline hold')
    return root


def run(folder,timeout=300,offline_hold=5):
    prepared=pilot.load(folder/'sync_pilot.json')
    if prepared.get('schema_version')!=1:raise Failure('Unsupported prepared input')
    for name,h in prepared['files_sha256'].items():
        if pilot.digest(folder/name)!=h:raise Failure('Prepared input changed: '+name)
    if prepared['sources_lf_sha256']!=pilot.source_hashes():raise Failure('Code changed; prepare a fresh pilot folder with the current branch')
    snapshot=folder/'unity_state.json'
    if snapshot.exists() or (folder/'active_run.json').exists():raise Failure('Single-use folder already used; preserve evidence and extract/prepare a NEW folder')
    capture=folder/'Captures'/('Capture_'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'_'+uuid.uuid4().hex[:8]);capture.mkdir(parents=True)
    for name in ('sync_pilot.json','server_seed.json','alignment.json'):shutil.copyfile(folder/name,capture/name)
    shutil.copyfile(folder/'server_seed.json',capture/'server_db.json')
    shutil.copyfile(__file__,capture/'recovery_observer.py')
    run_id=uuid.uuid4().hex
    report={'schema_version':1,'kind':'native_offline_point_recovery','run_id':run_id,'completed':False,'result':'FAILED',
            'scope':'One native Unity durable offline point update and recovery against a private real HTTP server.',
            'started_utc':datetime.now(timezone.utc).isoformat(),'offline_hold_seconds':offline_hold,
            'not_measured':['lost responses','application/process restart','two-client conflicts','production backend','participant outcomes']}
    server=pilot.existing.LocalServer(capture/'server_db.json',capture,prepared['port'])
    def phase(name):pilot.write(folder/'active_run.json',{'prepared_id':prepared['prepared_id'],'run_id':run_id,'capture':capture.name,'phase':name})
    def snap(name):
        root=pilot.load(snapshot);remote=server.request(pilot.FEATURES)
        pilot.write(capture/('unity_'+name+'.json'),root);pilot.write(capture/('server_'+name+'.json'),remote)
        return root,remote
    try:
        server.start();report['initial_server_pid']=server.process.pid;phase('starting');seed=pilot.feature_map(pilot.load(folder/'server_seed.json'))
        print('\nBefore Play: Use Localhost ON; JsonSpawner Save File Path must be this FILE:\n'+str(snapshot),flush=True)
        print('Keep the normal GeoJSON/alignment. Play RUNNING and UNPAUSED. Waiting for the private fixture...',flush=True)
        pilot.wait_until('private acknowledged fixture',lambda:pilot.settled(pilot.load(snapshot),seed),timeout,3)
        baseline,_=snap('baseline')
        if pilot.feature_writes(pilot.records(server.audit)):raise Failure('Unexpected startup feature write')
        phase('awaiting_noop_save')
        print('\nSTEP 1: Select CoordinatesLogic. Tools > Experiments > Synchronization > Record Unchanged Save.',flush=True)
        def recorded_save():
            actions=pilot.records(capture/'unity_actions.jsonl')
            if not actions:return False
            if len(actions)!=1 or actions[0].get('kind')!='unchanged_save_requested' or actions[0].get('run_id')!=run_id or pilot.feature_map({'features':actions[0]['features_before']})!=seed:
                raise Failure('Invalid native unchanged-save marker')
            cfg=pilot.configuration(capture,'noop',run_id,prepared['port'])
            root=pilot.load(snapshot);pilot.require_noop(root,server.request(pilot.FEATURES),baseline,pilot.records(server.audit))
            return cfg if root.get('lastModified')!=baseline.get('lastModified') else False
        cfg=pilot.wait_until('native unchanged save',recorded_save,timeout)
        phase('observing_noop');duration=pilot.observation_seconds(cfg,5);start=time.monotonic();noop_start=len(pilot.records(server.audit))
        while time.monotonic()-start<duration:
            pilot.require_noop(pilot.load(snapshot),server.request(pilot.FEATURES),baseline,pilot.records(server.audit));time.sleep(.25)
        if len(pilot.sync_reads(pilot.records(server.audit)[noop_start:]))<2:raise Failure('Insufficient actual Unity polling during no-op observation')
        snap('noop');report['noop']={'passed':True,'observation_seconds':time.monotonic()-start,'observation_audit_start':noop_start,'audit_end':len(pilot.records(server.audit))}
        phase('awaiting_edit')
        print('\nSTEP 2: Select CoordinatesLogic > Tools > Experiments > Synchronization > Begin Point Edit.',flush=True)
        print('WAIT. Do NOT move the point until Python prints OFFLINE READY.',flush=True)
        def edit_marker():
            actions=pilot.records(capture/'unity_actions.jsonl')
            if len(actions)<2:return False
            if len(actions)!=2 or actions[1].get('kind')!='begin_point_edit' or actions[1].get('run_id')!=run_id or pilot.feature_map({'features':actions[1]['features_before']})!=seed:
                raise Failure('Invalid native begin-edit marker')
            if pilot.configuration(capture,'edit',run_id,prepared['port'])!=cfg:raise Failure('Configuration changed between actions')
            return True
        pilot.wait_until('native begin-edit marker',edit_marker,timeout)
        server.stop()
        if pilot.feature_map(pilot.load(snapshot))!=seed or pilot.feature_writes(pilot.records(server.audit)):
            raise Failure('The point was edited before the server was stopped; wait for OFFLINE READY on the next fresh run')
        report['offline_started_utc']=datetime.now(timezone.utc).isoformat();phase('offline_edit')
        print('\nOFFLINE READY: move ONLY fire point '+prepared['target_id']+' once with the normal grab interaction, then RELEASE.',flush=True)
        print('Keep Play running/unpaused. Connection warnings are expected. The observer will reconnect automatically after a durable pending edit is verified.',flush=True)
        frozen=None
        with (capture/'offline_observations.jsonl').open('w',encoding='utf-8') as evidence:
            def pending_edit():
                nonlocal frozen
                root=offline_edit(pilot.load(snapshot),baseline,prepared['target_id'],frozen)
                if root:
                    if frozen is None:frozen=copy.deepcopy(pilot.feature_map(root))
                    evidence.write(pilot.json.dumps({'monotonic_seconds':time.monotonic(),'snapshot':root},allow_nan=False)+'\n')
                return root
            pending=pilot.wait_until('durable offline target update',pending_edit,timeout,offline_hold)
        pilot.write(capture/'unity_offline.json',pending);offline_remote=pilot.load(capture/'server_db.json')
        if pilot.feature_map(offline_remote)!=seed:raise Failure('Server changed during the outage')
        pilot.write(capture/'server_offline.json',offline_remote)
        report['offline']={'passed':True,'pending_updates':1,'next_revision':pilot.existing.outbox(pending)['nextRevision'],
                           'audit_end':len(pilot.records(server.audit))}
        phase('recovering');server.start();report['recovered_server_pid']=server.process.pid;health=time.monotonic();first=None
        print('\nRECOVERY: private API health confirmed. Leave the point and map still; waiting for real delivery and empty outbox...',flush=True)
        def recovered():
            nonlocal first
            root,remote=pilot.load(snapshot),server.request(pilot.FEATURES)
            if pilot.feature_map(root)!=frozen:raise Failure('Offline edited feature data changed during recovery')
            ok=pilot.require_edit(root,remote,baseline,prepared['target_id'],pilot.records(server.audit))
            if ok and first is None:first=time.monotonic()
            if not ok:first=None
            return ok
        confirmation=pilot.observation_seconds(cfg,3)
        pilot.wait_until('offline update delivered and acknowledged',recovered,timeout,confirmation)
        snap('recovered');audit=pilot.records(server.audit)
        problems=pilot.existing.observer_problems(audit,0)
        if problems:raise Failure('; '.join(problems))
        report['recovery']={'passed':True,'feature_writes':1,'outbox_empty':True,'quiet_confirmation_seconds':confirmation,
                            'observed_convergence_after_health_seconds':first-health,'observation_resolution_seconds':.25,
                            'timing_scope':'Observed saved-local/server/outbox convergence after independent API health check; excludes confirmation window. Not instrumented Unity callback latency.'}
        report.update(completed=True,result='PASSED');print('\nPASSED. Stop Play and restore original Inspector/API settings. Capture: '+str(capture),flush=True)
        return capture
    except (Exception,KeyboardInterrupt) as error:
        report['error']=str(error) or type(error).__name__;print('FAILED: '+report['error']+'\nFailed capture: '+str(capture),file=sys.stderr,flush=True);raise
    finally:
        server.stop();phase('finished' if report['completed'] else 'failed')
        if snapshot.exists():shutil.copyfile(snapshot,capture/'unity_final.json')
        report['finished_utc']=datetime.now(timezone.utc).isoformat()
        report['files_sha256']={p.name:pilot.digest(p) for p in capture.iterdir() if p.is_file() and p.name!='report.json'}
        pilot.write(capture/'report.json',report)


def analyze(capture):
    report=pilot.load(capture/'report.json')
    if report.get('kind')!='native_offline_point_recovery' or not report.get('completed') or report.get('result')!='PASSED':raise Failure('Incomplete/failed recovery capture')
    for name,h in report['files_sha256'].items():
        if Path(name).name!=name or pilot.digest(capture/name)!=h:raise Failure('Evidence changed: '+name)
    baseline=pilot.load(capture/'unity_baseline.json');seed=pilot.feature_map(pilot.load(capture/'server_seed.json'))
    if not pilot.settled(baseline,seed) or pilot.feature_map(pilot.load(capture/'server_baseline.json'))!=seed:raise Failure('Baseline differs from declared private fixture')
    prepared=pilot.load(capture/'sync_pilot.json');target=prepared['target_id'];cfg=pilot.configuration(capture,'noop',report['run_id'],prepared['port'])
    if cfg!=pilot.configuration(capture,'edit',report['run_id'],prepared['port']):raise Failure('Settings changed')
    audit=pilot.records(capture/'requests.jsonl',partial=False);noop=report['noop'];offline=report['offline']
    bounds=[noop['observation_audit_start'],noop['audit_end'],offline['audit_end']]
    if bounds!=sorted(bounds) or any(type(i) is not int or i<0 or i>len(audit) for i in bounds):raise Failure('Invalid audit boundaries')
    if noop['observation_seconds']<pilot.observation_seconds(cfg,5) or len(pilot.sync_reads(audit[bounds[0]:bounds[1]]))<2:raise Failure('Insufficient unchanged observation')
    pilot.require_noop(pilot.load(capture/'unity_noop.json'),pilot.load(capture/'server_noop.json'),baseline,audit[:bounds[1]])
    if pilot.feature_writes(audit[:bounds[2]]):raise Failure('Feature uploaded before recovery')
    actions=pilot.records(capture/'unity_actions.jsonl',partial=False)
    if len(actions)!=2 or [a.get('kind') for a in actions]!=['unchanged_save_requested','begin_point_edit'] or any(a.get('run_id')!=report['run_id'] or pilot.feature_map({'features':a['features_before']})!=seed for a in actions):raise Failure('Invalid native action evidence')
    pending=pilot.load(capture/'unity_offline.json');offline_edit(pending,baseline,target)
    observations=pilot.records(capture/'offline_observations.jsonl',partial=False)
    times=[o['monotonic_seconds'] for o in observations]
    if len(times)<2 or any(not pilot.math.isfinite(t) for t in times) or times!=sorted(times) or times[-1]-times[0]<report['offline_hold_seconds']:
        raise Failure('Insufficient timestamped durable offline observations')
    for observation in observations:offline_edit(observation['snapshot'],baseline,target,pilot.feature_map(pending))
    if pilot.feature_map(pilot.load(capture/'server_offline.json'))!=seed:raise Failure('Offline server changed')
    root=pilot.load(capture/'unity_recovered.json');remote=pilot.load(capture/'server_recovered.json')
    if pilot.feature_map(root)!=pilot.feature_map(pending) or not pilot.require_edit(root,remote,baseline,target,audit):raise Failure('Recovery did not deliver the durable offline payload')
    if pilot.feature_map(pilot.load(capture/'unity_final.json'))!=pilot.feature_map(root) or pilot.existing.outbox(pilot.load(capture/'unity_final.json'))['pending']:raise Failure('Final Unity state differs')
    if pilot.feature_map(pilot.load(capture/'server_db.json'))!=pilot.feature_map(remote):raise Failure('Final server disk differs')
    if report['offline_hold_seconds']<5:raise Failure('Offline hold shorter than declared native minimum')
    convergence=report['recovery']['observed_convergence_after_health_seconds']
    if not pilot.math.isfinite(convergence) or convergence<0:raise Failure('Invalid recovery observation time')
    if report['recovery']['quiet_confirmation_seconds']<pilot.observation_seconds(cfg,3):raise Failure('Recovery confirmation too short')
    if pilot.existing.observer_problems(audit,0):raise Failure('Incorrect observer role publication')
    return {'result':'PASSED','durable_offline_updates':1,'confirmed_recovery_updates':1,'outbox_empty':True,
            'observed_convergence_after_health_seconds':report['recovery']['observed_convergence_after_health_seconds'],
            'not_measured':report['not_measured']}


def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__);mode=p.add_mutually_exclusive_group(required=True)
    mode.add_argument('--run',type=Path);mode.add_argument('--analyze-run',type=Path);mode.add_argument('--pack-run',type=Path)
    p.add_argument('--timeout',type=float,default=300);args=p.parse_args(argv)
    try:
        if args.run:
            if not pilot.math.isfinite(args.timeout) or args.timeout<=0:p.error('--timeout must be finite and positive')
            capture=run(args.run.resolve(),args.timeout);pilot.pack(capture)
        elif args.analyze_run:print(pilot.json.dumps(analyze(args.analyze_run.resolve()),indent=2))
        else:pilot.pack(args.pack_run.resolve())
        return 0
    except KeyboardInterrupt:return 130
    except (OSError,ValueError,KeyError,Failure) as error:print('FAILED: '+str(error),file=sys.stderr);return 1


if __name__=='__main__':sys.exit(main())
