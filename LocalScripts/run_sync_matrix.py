#!/usr/bin/env python3
"""Repeated native-Unity synchronization matrix. Python NEVER writes Unity state.
Uses production APIs via an explicitly attached Editor command bridge. No UI,
standalone process-crash, production backend or geographic accuracy claims.
"""
import argparse
import copy
from datetime import datetime,timezone
import json
import math
from pathlib import Path
import shutil
import subprocess
import struct
import sys
import time
import uuid
import run_sync_pilot as p

CASES=('online_create_delete','offline_create_delete','play_restart','lost_create_response','server_error','conflicting_edit','conflicting_delete')
BRIDGE=p.ASSETS/'TRIFFID/Editor/Experiments/SyncMatrixBridge.cs'

def require(value,message):
    if not value:raise p.existing.TestFailure(message)

def alignment_key(config):
    transform=config['colmap_to_enu'];f32=lambda v:struct.unpack('f',struct.pack('f',v))[0]
    return (config['origin_wgs84'],f32(transform['scale']),[f32(v) for v in transform['R_rowmajor']],[f32(v) for v in transform['t']])

def require_restart_lifecycle(events):
    # Unity emits EnteredEditMode after leaving Play. There is no
    # ExitedPlayMode member in PlayModeStateChange.
    states=[event['state'] for event in events]
    require('EnteredEditMode' in states and 'EnteredPlayMode' in states and
            states.index('EnteredEditMode')<states.index('EnteredPlayMode'),
            'Missing native Play lifecycle')


def trial_audit(case,audit):
    writes=p.feature_writes(audit)
    if case.startswith('conflicting_'):require(not writes,'Conflicting edit was uploaded instead of server-wins')
    elif case in ('online_create_delete','offline_create_delete'):
        require(len(writes)==2 and sorted(w['method'] for w in writes)==['DELETE','PUT'] and all(w['status']==200 for w in writes),'Unexpected CRUD writes')
    elif case=='play_restart':
        require(len(writes)==1 and writes[0]['method']=='PATCH' and writes[0]['status']==200,'Unexpected restart writes')
    elif case=='lost_create_response':
        require(len(writes)==1 and writes[0]['method']=='PUT' and writes[0].get('outcome')=='response_dropped_after_commit','Lost response did not fire or create was duplicated')
    elif case=='server_error':
        require(len(writes)==2 and [w['status'] for w in writes]==[503,200] and all(w['method']=='PATCH' for w in writes),'503 retry not observed exactly once')

class Server(p.existing.LocalServer):
    def start(self):
        self.require_free_port();p.load(self.db)
        self.log=(self.run_dir/'server.log').open('a',encoding='utf-8')
        self.process=subprocess.Popen([sys.executable,'-u',str(Path(__file__).with_name('sync_matrix_server.py')),
            '--db',str(self.db),'--folder',str(self.run_dir),'--port',str(self.port)],stdout=self.log,stderr=subprocess.STDOUT)
        until=time.monotonic()+10
        while time.monotonic()<until:
            require(self.process.poll() is None,'Private matrix server exited')
            try:self.request(p.FEATURES);return
            except OSError:time.sleep(.1)
        raise p.existing.TestFailure('Private matrix server startup timed out')


def final_oracle(case,baseline,edited,local,remote,target,created):
    """Exact preservation for all original unrelated data. Aliases only for create."""
    old,l,r=map(p.feature_map,(baseline,local,remote));state=p.existing.outbox(local)
    require(not state['pending'],'Pending operations remain')
    conflict=case.startswith('conflicting_')
    original=set(old)-({target} if case!='lost_create_response' else set())
    for fid in original:require(l.get(fid)==old[fid] and r.get(fid)==old[fid],'Unrelated feature changed: '+fid)
    if conflict:
        expected=copy.deepcopy(old)
        if case=='conflicting_delete':expected.pop(target)
        else:expected[target]['properties']['matrix_external']='second-client-edit'
        require(l==r==expected,'Server-wins conflict outcome differs')
    elif case in ('online_create_delete','offline_create_delete','lost_create_response'):
        deleting=case!='lost_create_response'
        if deleting:require(target not in l and target not in r and target not in state['remoteIds'],'Deletion not acknowledged')
        require(len(l)==len(old)+(0 if deleting else 1) and len(r)==len(l),'Missing or duplicate created feature')
        remote_id=state['remoteIds'].get(created)
        matches=[f for f in remote['features'] if str(p.existing.client_identity(f))==created or str(f['id'])==str(remote_id)]
        require(len(matches)==1 and remote_id,'Created feature has no unique binding')
        incoming=p.feature_map(edited)[created]
        local_new=l.get(created) or l.get(str(remote_id))
        require(local_new is not None and p.existing.comparable(incoming)==p.existing.comparable(local_new)==p.existing.comparable(matches[0]),'Created feature content changed')
    else:
        expected=p.feature_map(edited)
        require(l==r==expected,'Edited feature differs after recovery')
    return True

def offline_oracle(case,baseline,edited,value,target,created):
    expected=copy.deepcopy(p.feature_map(edited));state=p.existing.outbox(value);initial=p.existing.outbox(baseline)
    if case=='offline_create_delete':expected.pop(target)
    require(p.feature_map(value)==expected,'Offline feature data differs from intended edits')
    pending=state['pending'];kinds=sorted(x['kind'] for x in pending)
    require(kinds==(['create','delete'] if case=='offline_create_delete' else ['update']),'Offline pending operations differ')
    require(state['nextRevision']==initial['nextRevision']+len(pending),'Unexpected offline revision count')
    for op in pending:
        fid=str(op['featureId'])
        require(fid==(created if op['kind']=='create' else target),'Wrong pending feature ID')
        if op['kind']!='delete':require(op.get('payload')==expected[fid],'Pending payload differs from saved edit')
        if op['kind']!='create':require(op.get('baseHash')==initial['acknowledgedHashes'].get(target),'Lost acknowledged baseline')
    return True


def run(folder,repetitions=20,timeout=120,cases=CASES):
    folder=folder.resolve();prepared=p.load(folder/'sync_pilot.json')
    require(prepared['sources_lf_sha256']==p.source_hashes(),'Prepared source mismatch; use current preparation')
    for n,h in prepared['files_sha256'].items():require(p.digest(folder/n)==h,'Prepared input changed: '+n)
    require(not (folder/'unity_state.json').exists() and not (folder/'matrix_run.json').exists(),'Use a NEW extracted input folder')
    capture=folder/'Captures'/('Matrix_'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'_'+uuid.uuid4().hex[:8]);capture.mkdir(parents=True)
    for n in ('sync_pilot.json','alignment.json','server_seed.json'):shutil.copyfile(folder/n,capture/n)
    for src in (Path(__file__),Path(__file__).with_name('sync_matrix_server.py'),BRIDGE):shutil.copyfile(src,capture/src.name)
    snapshot=folder/'unity_state.json';seed=p.load(folder/'server_seed.json');target=prepared['target_id'];seq=0;runid=uuid.uuid4().hex
    report={'schema_version':1,'kind':'native_sync_matrix','run_id':runid,'completed':False,'cases':list(cases),'repetitions':repetitions,'trials':[],
        'sources_lf_sha256':p.source_hashes(),'started_utc':datetime.now(timezone.utc).isoformat(),
        'not_measured':['controller UI','process crash/kill','production backend','read-update race','retry exhaustion','physical geographic accuracy']}
    p.write(folder/'matrix_run.json',{'schema_version':1,'kind':'native_sync_matrix','run_id':runid,'port':prepared['port']})
    server=Server(capture/'server_db.json',capture,prepared['port'])
    def root():return p.load(snapshot)
    def command(kind,**kw):
        nonlocal seq
        seq+=1;p.write(folder/'matrix_command.json',dict(run_id=runid,seq=seq,kind=kind,**kw))
        def ack():
            value=p.load(folder/'matrix_response.json')
            if value.get('run_id')!=runid or value.get('seq')!=seq:return False
            require(value.get('ok'),value.get('error','Unity command failed'));return value
        result=p.wait_until('Unity command '+kind,ack,timeout)
        p.write(capture/('command_%04d.json'%seq),{'request':dict(kind=kind,**kw),'response':result})
        return result
    def settled_seed():return p.settled(root(),p.feature_map(seed))
    def hold_pending(path,check):
        start=None;observations=[]
        def observe():
            nonlocal start
            value=root()
            if not check(value):
                start=None;observations.clear();return False
            now=time.monotonic();start=now if start is None else start
            observations.append({'monotonic_seconds':now,'snapshot':value})
            return value if now-start>=5 else False
        value=p.wait_until('five seconds durable offline state',observe,timeout)
        path.write_text('\n'.join(json.dumps(o) for o in observations)+'\n');return value
    try:
        p.write(server.db,seed);server.start()
        print('\nBefore Play: set Use Localhost ON, coordinator/remote polling ON, and Save File Path to this FILE:\n'+str(snapshot),flush=True)
        print('Keep normal GeoJSON/alignment. Play unpaused; select CoordinatesLogic and choose Tools > Experiments > Synchronization > Attach Automated Matrix.\nDo not edit. The batch may stop/start Play automatically. No Enter is needed.',flush=True)
        cfg=command('config');report['configuration']=cfg
        require(cfg['coordinator']['enableRemotePolling'],'Remote polling disabled')
        require(alignment_key(cfg['alignment'])==alignment_key(p.load(folder/'alignment.json')),'Scene alignment differs from fixture')
        quiet=max(5,2*max(2,cfg['coordinator']['remotePollingIntervalSeconds'])+cfg['coordinator']['localChangeGraceSeconds']+cfg['coordinator']['updateDebounceSeconds']+cfg['coordinator']['localScanIntervalSeconds']+1)
        p.wait_until('initial private fixture',settled_seed,timeout,2)
        baseline=root();audit0=len(p.records(server.audit));command('noop')
        def unchanged():
            p.require_noop(root(),server.request(p.FEATURES),baseline,p.records(server.audit)[audit0:]);return True
        p.wait_until('initial no-op confirmation',unchanged,timeout,quiet)
        p.write(capture/'initial_baseline.json',baseline);p.write(capture/'initial_noop.json',root());report['noop_audit_start']=audit0;report['noop_audit_end']=len(p.records(server.audit));report['quiet_seconds']=quiet
        for case in cases:
            for repetition in range(repetitions):
                name=case+'_%02d'%(repetition+1);trial=capture/name;trial.mkdir()
                print('RUN '+name+' / '+str(repetitions)+' per case',flush=True)
                trial_cfg=command('config')
                require(all(trial_cfg[k]==cfg[k] for k in ('coordinator','alignment','unity_version')),'Settings/runtime changed during matrix')
                server.stop();p.write(server.db,seed);p.write(capture/'fault.json',{'mode':'none','remaining':0});server.start();command('refresh')
                p.wait_until('clean baseline',settled_seed,timeout,2);baseline=root();p.write(trial/'baseline.json',baseline)
                audit_start=len(p.records(server.audit));created=None;offline=case in ('offline_create_delete','play_restart','conflicting_edit','conflicting_delete')
                if offline:server.stop()
                if case in ('server_error','lost_create_response'):p.write(capture/'fault.json',{'mode':'server_error' if case=='server_error' else 'lost_response','remaining':1})
                if case in ('online_create_delete','offline_create_delete','lost_create_response'):
                    created=command('create',feature_id=target)['created_id']
                    edited={'features':p.load(folder/'matrix_response.json')['features_after']}
                    if case!='lost_create_response':command('delete',feature_id=target)
                else:
                    moved=command('move',feature_id=target);edited={'features':moved['features_after']}
                p.write(trial/'edited.json',edited)
                if offline:
                    def pending_ok(value):
                        try:return offline_oracle(case,baseline,edited,value,target,created)
                        except p.existing.TestFailure:return False
                    off=hold_pending(trial/'offline.jsonl',pending_ok);p.write(trial/'offline.json',off)
                    remote_offline=p.load(server.db);require(p.feature_map(remote_offline)==p.feature_map(seed),'Server changed during offline hold')
                    p.write(trial/'server_offline.json',remote_offline)
                    if case=='play_restart':
                        life_start=len(p.records(folder/'matrix_lifecycle.jsonl'))
                        stopped=command('stop_play')
                        p.wait_until('Play exited',lambda:any(x['state']=='EnteredEditMode' for x in p.records(folder/'matrix_lifecycle.jsonl')[life_start:]),timeout)
                        command('start_play')
                        p.wait_until('Play reentered',lambda:any(x['state']=='EnteredPlayMode' for x in p.records(folder/'matrix_lifecycle.jsonl')[life_start:]),timeout)
                        after=command('config')
                        p.wait_until('restored pending edit',lambda:pending_ok(root()),timeout,2)
                        p.write(trial/'after_play_restart.json',root());p.write(trial/'restart_evidence.json',{'stop':stopped,'after':after,'lifecycle':p.records(folder/'matrix_lifecycle.jsonl')[life_start:]})
                    if case.startswith('conflicting_'):
                        # Only server-owned data is changed by Python, before reconnect.
                        external=copy.deepcopy(seed)
                        if case=='conflicting_delete':external['features']=[f for f in external['features'] if str(f['id'])!=target]
                        else:next(f for f in external['features'] if str(f['id'])==target)['properties']['matrix_external']='second-client-edit'
                        p.write(server.db,external);p.write(trial/'external_server.json',external)
                    server.start()
                health=time.monotonic();first=None;last=None
                def converged():
                    nonlocal first,last
                    local=root();remote=server.request(p.FEATURES)
                    try:final_oracle(case,baseline,edited,local,remote,target,created)
                    except p.existing.TestFailure:
                        first=None;return False
                    first=first or time.monotonic();last=(local,remote);return True
                p.wait_until('exact convergence and quiet confirmation '+name,converged,timeout,quiet)
                local,remote=last;p.write(trial/'local_final.json',local);p.write(trial/'server_final.json',remote)
                audit_end=len(p.records(server.audit));trial_audit(case,p.records(server.audit)[audit_start:audit_end])
                result={'case':case,'repetition':repetition+1,'target':target,'created':created,'audit_start':audit_start,'audit_end':audit_end,'observed_convergence_seconds':first-health,'confirmation_seconds':quiet,'observation_resolution_seconds':.25,'timing_scope':'From API health after reconnect for offline cases; from observation start after commands for online cases. Not callback latency.','passed':True}
                p.write(trial/'trial.json',result);report['trials'].append(result)
                print('PASS '+name,flush=True)
        report['completed']=True;report['result']='PASSED';print('PASSED MATRIX. Detach Automated Matrix, stop Play, restore original Save File Path/API settings.\nCapture: '+str(capture),flush=True)
    except BaseException as error:
        report['error']=str(error) or type(error).__name__;raise
    finally:
        server.stop();report['finished_utc']=datetime.now(timezone.utc).isoformat()
        if snapshot.exists():shutil.copyfile(snapshot,capture/'unity_final.json')
        report['files_sha256']={str(x.relative_to(capture)):p.digest(x) for x in capture.rglob('*') if x.is_file() and x.name!='report.json'}
        p.write(capture/'report.json',report)
        print('Retained capture: '+str(capture),flush=True)
    analyze(capture);pack(capture);return capture


def analyze(capture):
    capture=Path(capture);m=p.load(capture/'report.json');require(m.get('kind')=='native_sync_matrix','Wrong capture kind')
    for n,h in m['files_sha256'].items():
        path=(capture/n).resolve();require(path.is_relative_to(capture.resolve()) and p.digest(path)==h,'Evidence changed: '+n)
    require(m.get('completed') and m.get('result')=='PASSED','Incomplete/failed matrix')
    require(m['repetitions']>=1 and m['cases'] and all(c in CASES for c in m['cases']) and len(set(m['cases']))==len(m['cases']),'Invalid case plan')
    require(len(m['trials'])==len(m['cases'])*m['repetitions'],'Incomplete trial coverage')
    keys={(x['case'],x['repetition']) for x in m['trials']};require(keys=={(c,i) for c in m['cases'] for i in range(1,m['repetitions']+1)},'Duplicate/missing trial')
    audit=p.records(capture/'requests.jsonl',partial=False)
    baseline=p.load(capture/'initial_baseline.json');p.require_noop(p.load(capture/'initial_noop.json'),baseline,baseline,audit[m['noop_audit_start']:m['noop_audit_end']])
    for t in m['trials']:
        folder=capture/(t['case']+'_%02d'%t['repetition']);final_oracle(t['case'],p.load(folder/'baseline.json'),p.load(folder/'edited.json'),p.load(folder/'local_final.json'),p.load(folder/'server_final.json'),t['target'],t['created'])
        require(0<=t['audit_start']<=t['audit_end']<=len(audit),'Invalid audit bounds')
        trial_audit(t['case'],audit[t['audit_start']:t['audit_end']])
        require(t['confirmation_seconds']>=m['quiet_seconds'] and math.isfinite(t['observed_convergence_seconds']) and t['observed_convergence_seconds']>=0,'Invalid timing')
        if (folder/'offline.jsonl').exists():
            obs=p.records(folder/'offline.jsonl',partial=False);times=[o['monotonic_seconds'] for o in obs]
            require(len(times)>1 and times==sorted(times) and times[-1]-times[0]>=5,'Insufficient offline hold')
            frozen=p.feature_map(p.load(folder/'offline.json'))
            require(all(p.feature_map(o['snapshot'])==frozen for o in obs),'Offline feature drift')
            for observation in obs:offline_oracle(t['case'],p.load(folder/'baseline.json'),p.load(folder/'edited.json'),observation['snapshot'],t['target'],t['created'])
            require(p.feature_map(p.load(folder/'server_offline.json'))==p.feature_map(p.load(folder/'baseline.json')),'Server changed while offline')
        if t['case']=='play_restart':
            evidence=p.load(folder/'restart_evidence.json');require_restart_lifecycle(evidence['lifecycle'])
            require(p.feature_map(p.load(folder/'after_play_restart.json'))==p.feature_map(p.load(folder/'edited.json')) and len(p.existing.outbox(p.load(folder/'after_play_restart.json'))['pending'])==1,'Pending edit not restored')
    last=m['trials'][-1];last_folder=capture/(last['case']+'_%02d'%last['repetition'])
    final=p.load(capture/'unity_final.json')
    require(p.feature_map(final)==p.feature_map(p.load(last_folder/'local_final.json')) and not p.existing.outbox(final)['pending'],'Final local state changed after trials')
    require(p.feature_map(p.load(capture/'server_db.json'))==p.feature_map(p.load(last_folder/'server_final.json')),'Final server disk differs')
    return {'result':'PASSED','trials':len(m['trials']),'cases':m['cases'],'repetitions':m['repetitions'],'not_measured':m['not_measured']}


def pack(capture):
    capture=Path(capture);archive=capture.with_suffix('.zip')
    import zipfile
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as z:
        for path in sorted(capture.rglob('*')):
            if path.is_file():z.write(path,str(Path(capture.name)/path.relative_to(capture)))
    print('Archive: '+str(archive),flush=True);return archive


def main():
    a=argparse.ArgumentParser(description=__doc__);g=a.add_mutually_exclusive_group(required=True);g.add_argument('--run',type=Path);g.add_argument('--analyze-run',type=Path);g.add_argument('--pack-run',type=Path)
    a.add_argument('--repetitions',type=int,default=20);a.add_argument('--timeout',type=float,default=180);a.add_argument('--cases',nargs='+',choices=CASES,default=list(CASES));v=a.parse_args()
    if v.repetitions<1 or not math.isfinite(v.timeout) or v.timeout<=0 or len(v.cases)!=len(set(v.cases)):a.error('Positive repetitions/finite timeout and unique cases required')
    try:
        if v.run:run(v.run,v.repetitions,v.timeout,tuple(v.cases))
        elif v.analyze_run:print(json.dumps(analyze(v.analyze_run),indent=2))
        else:pack(v.pack_run)
    except (OSError,ValueError,KeyError,p.existing.TestFailure) as e:print('FAILED: '+str(e),file=sys.stderr);return 1
    return 0

if __name__=='__main__':sys.exit(main())
