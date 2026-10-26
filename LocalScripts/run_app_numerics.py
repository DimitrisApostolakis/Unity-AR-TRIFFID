#!/usr/bin/env python3
"""Verify/pack native App Numerics captures. Python 3.9+, standard library only.

Does not execute or simulate Unity physics, rendering or application networking.
"""
from __future__ import annotations
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import sys
import zipfile

SHAPES=('plane','slope','two_levels','miss','inactive','no_collider')
SAMPLES=((0,0),(-10,-10),(10,-10),(-10,10),(10,10))
POLYGONS={'ccw_exterior':True,'cw_exterior':True,'hole_clockwise':True,'closed_ring':True,
          'crossing':False,'zero_area':False,'repeated':False,'overlap':False,
          'too_few':False,'nonfinite_altitude':False,'longitude_range':False}
COUNTS=(0,10,10,100,100,1000,1000)
CONDITIONS=('idle','idle','save','idle','save','idle','save')


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def require(condition,message):
    if not condition: raise ValueError(message)


def finite(value):
    value=float(value)
    require(math.isfinite(value),'Nonfinite native measurement')
    return value


def bit(value):
    require(value in ('0','1'),'Invalid boolean observation')
    return value=='1'


def integrity(folder):
    meta=load(folder/'app_numerics.json')
    require(meta.get('schema_version')==1,'Unsupported capture schema')
    hashes=meta.get('files_sha256')
    require(isinstance(hashes,dict) and hashes,'Missing output hashes')
    for name,expected in hashes.items():
        path=(folder/name).resolve()
        require(path.is_relative_to(folder.resolve()),'Unsafe evidence path')
        require(path.is_file() and digest(path)==expected,'Evidence changed: '+name)
    sources=meta.get('source_hashes_lf')
    require(isinstance(sources,dict) and len(sources)>=3,'Missing source provenance')
    require(all(isinstance(h,str) and re.fullmatch('[a-f0-9]{64}',h) for h in sources.values()),'Malformed source hash')
    return meta


def distribution(values):
    if not values:return {'n':0}
    values=sorted(values)
    quantile=lambda p:values[max(0,math.ceil(p*len(values))-1)]
    return {'n':len(values),'median':statistics.median(values),'p95':quantile(.95),'p99':quantile(.99),
            'maximum':values[-1],'mean':statistics.mean(values),'exact_zeros':values.count(0.)}


def height_key(profile,pose,shape,enabled,east,north,height):
    return profile,int(pose),shape,bool(enabled),float(east),float(north),float(height)


def expected_height_keys():
    return {height_key(profile,pose,shape,enabled,e+(1000 if shape=='miss' else 0),n,h)
            for profile in ('identity','site') for pose in range(6) for shape in SHAPES
            for enabled in (True,False) for e,n in SAMPLES for h in (-2,0,2)}


def analyze_height(folder,meta):
    require(meta.get('height_budget_m')==.005,'Changed synthetic height budget')
    with (folder/'height_raw.csv').open(encoding='utf-8-sig',newline='') as f: rows=list(csv.DictReader(f))
    actual={};normal_errors=[];normal_failures=[];diagnostic={};invariance=[]
    for row in rows:
        key=height_key(row['calibration'],int(row['pose']),row['surface'],bit(row['collider_enabled']),
                       finite(row['east']),finite(row['north']),finite(row['expected_height_m']))
        require(key not in actual,'Duplicate height observation')
        expected_hit=row['surface'] not in ('miss','inactive','no_collider')
        require(bit(row['expected_hit'])==expected_hit,'Changed surface-hit oracle')
        scope='stress' if int(row['pose'])==5 else 'intended_floor_diagnostic' if row['surface']=='two_levels' else 'normal'
        require(row['scope']==scope,'Changed height scope')
        hit=bit(row['actual_hit'])
        height=finite(row['actual_height_m']) if hit else None
        if not hit:require(row['actual_height_m']=='' and row['error_m']=='','Miss contains a claimed height')
        error=None if height is None else height-finite(row['expected_height_m'])
        if hit:require(abs(finite(row['error_m'])-error)<1e-10,'Recorded height error differs from raw data')
        status=(hit and row['height_source']=='mesh' and row['height_status']==('below_surface' if height<0 else 'ok')) if expected_hit else (
            not hit and row['height_source']=='unavailable' and row['height_status']=='no_surface')
        require(bit(row['status_correct'])==status,'Height status result differs from raw evidence')
        passed=hit==expected_hit and status and bit(row['collider_restored']) and (not expected_hit or abs(error)<=.005)
        actual[key]=(row,height)
        if scope=='normal':
            if hit and expected_hit:normal_errors.append(abs(error))
            if not passed:normal_failures.append({'key':key,'hit':hit,'error_m':error,'status_correct':status,
                                                'collider_restored':bit(row['collider_restored'])})
        else:
            d=diagnostic.setdefault(scope,{'observations':0,'failures':0,'errors_m':[]})
            d['observations']+=1;d['failures']+=not passed
            if error is not None:d['errors_m'].append(abs(error))
    require(set(actual)==expected_height_keys(),'Incomplete/unexpected native height coverage')
    for key,(row,height) in actual.items():
        if row['scope']!='normal' or height is None:continue
        initial=actual[(key[0],0,*key[2:])][1]
        if initial is not None:invariance.append(abs(height-initial))
    poses=load(folder/'height_poses.json')
    require(len(poses)==12 and {(p['calibration'],p['pose']) for p in poses}=={(c,i) for c in ('identity','site') for i in range(6)},'Incomplete pose evidence')
    require(all(len(p['matrix_column_major'])==16 and all(math.isfinite(v) for v in p['matrix_column_major']) for p in poses),'Invalid pose matrix')
    polygons=load(folder/'polygons.json')
    require(len(polygons)==len(POLYGONS) and {p['id'] for p in polygons}==set(POLYGONS),'Incomplete polygon coverage')
    polygon_failures=[]
    for p in polygons:
        require(p['expected_accepted']==POLYGONS[p['id']],'Changed polygon acceptance oracle')
        accepted=p['accepted']
        require(type(accepted) is bool,'Invalid polygon acceptance')
        closed=not accepted or p['output'][0]==p['output'][-1]
        unchanged=accepted or p['input']==p['output']
        area=sum(a[0]*b[1]-b[0]*a[1] for a,b in zip(p['output'],p['output'][1:])) if accepted else 0
        winding=not accepted or (area>0 if p['exterior'] else area<0)
        indices=[p['input'].index(vertex) if vertex in p['input'] else -1 for vertex in p['output']] if accepted else list(range(len(p['input'])))
        metadata=all(i>=0 for i in indices) and p['heights']==[100+i for i in indices] and p['sources']==['v'+str(i) for i in indices] and p['statuses']==['v'+str(i) for i in indices]
        require(p['metadata_correct']==metadata,'Polygon metadata result differs from recorded arrays')
        passed=accepted==POLYGONS[p['id']] and closed and winding and unchanged and metadata
        require(p['closure_correct']==closed and p['winding_correct']==winding and p['rejected_input_unchanged']==unchanged and p['passed']==passed,
                'Polygon outcome differs from evidence: '+p['id'])
        if not passed:polygon_failures.append(p['id'])
    log=(folder/'core_checks.txt').read_text(encoding='utf-8-sig')
    totals=re.findall(r'^RESULT: (\d+) passed, (\d+) failed\s*$',log,re.M)
    require(len(totals)==1,'Missing native core-check totals')
    core_passed,core_failed=map(int,totals[0])
    require(len(re.findall(r'^PASS ',log,re.M))==core_passed and len(re.findall(r'^FAIL ',log,re.M))==core_failed,'Core-check log counts disagree')
    require((meta.get('core_result_code')==0)==(core_failed==0),'Core-check exit code disagrees')
    for d in diagnostic.values():d['absolute_error_m']=distribution(d.pop('errors_m'))
    invariance_failures=sum(v>.005 for v in invariance)
    return {'result':'PASSED_NORMAL' if not normal_failures and not polygon_failures and not core_failed and not invariance_failures else 'FAILED_NORMAL',
            'height_observations':len(rows),'normal_absolute_height_error_m':distribution(normal_errors),
            'normal_height_invariance_m':distribution(invariance),'normal_height_failures':normal_failures,
            'normal_invariance_exceedances':invariance_failures,'diagnostics':diagnostic,
            'polygon_fixtures':len(polygons),'polygon_failures':polygon_failures,
            'native_core_checks':{'passed':core_passed,'failed':core_failed,'real_http':False},
            'not_measured':['physical geographic accuracy','real HTTP fault reliability','manual controller replay','rendering performance','participant outcomes']}


def analyze_performance(folder,meta):
    require(meta.get('extra_feature_counts')==list(COUNTS) and meta.get('conditions')==list(CONDITIONS),'Changed workload plan')
    require(meta.get('warmup_seconds')==3 and meta.get('measurement_seconds_per_phase')==15,'Changed phase durations')
    with (folder/'frames.csv').open(encoding='utf-8-sig',newline='') as f:rows=list(csv.DictReader(f))
    groups={i:[] for i in range(7)};phase_rows={i:[] for i in range(7)};previous_frame=-1;previous_phase=-1;previous_time=-1
    for row in rows:
        phase=int(row['phase']);frame=int(row['frame']);elapsed=finite(row['phase_seconds'])
        require(phase in groups and phase>=previous_phase and frame>previous_frame,'Invalid frame/phase order')
        require(int(row['extra_features'])==COUNTS[phase] and row['condition']==CONDITIONS[phase],'Frame workload differs from plan')
        if phase==previous_phase:require(elapsed>=previous_time,'Phase time went backwards')
        previous_frame,previous_phase,previous_time=frame,phase,elapsed
        require(bit(row['warmup'])==(elapsed<3),'Incorrect warm-up classification')
        require(finite(row['frame_ms'])>0,'Nonpositive frame interval')
        require(int(row['allocated_bytes'])>=0 and int(row['mono_used_bytes'])>=0,'Negative memory observation')
        for k in ('camera_x','camera_y','camera_z','camera_qx','camera_qy','camera_qz','camera_qw'):finite(row[k])
        bit(row['save_in_progress'])
        phase_rows[phase].append(row)
        if 3<=elapsed<=18:groups[phase].append(row)
    events=[json.loads(line) for line in (folder/'events.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    summaries=[]
    for phase in range(7):
        group=groups[phase];require(len(group)>=2,'Insufficient measured frames in phase '+str(phase))
        records=[e for e in events if e['phase']==phase]
        starts=[e for e in records if e['kind']=='phase_start'];ends=[e for e in records if e['kind']=='phase_end']
        require(len(starts)==len(ends)==1,'Incomplete performance phase '+str(phase))
        event_duration=finite(ends[0]['editor_seconds'])-finite(starts[0]['editor_seconds'])
        require(event_duration>0,'Invalid performance event order')
        # Completion uses the collector's phase clock, also recorded in every
        # frame. Legacy phase_start events were written after that clock began;
        # their timestamp difference is not the phase duration.
        elapsed_end=finite(phase_rows[phase][-1]['phase_seconds'])
        require(elapsed_end>=18 and ends[0]['observed_frames']==len(phase_rows[phase]),'Incomplete performance phase '+str(phase))
        clock_start=starts[0].get('phase_clock_start_seconds')
        if clock_start is not None:
            require(finite(starts[0]['editor_seconds'])>=finite(clock_start) and
                    finite(ends[0]['editor_seconds'])>=finite(clock_start)+elapsed_end,'Inconsistent performance phase clock')
        require(starts[0]['extra_features']==COUNTS[phase] and starts[0]['condition']==CONDITIONS[phase],'Incorrect phase metadata')
        require(COUNTS[phase]==0 or starts[0]['fixture_renderers']>0,'No active workload renderers')
        geometry_counts={}
        workload=[]
        if COUNTS[phase]>0:
            workload=load(folder/('phase_'+str(phase))/'fixture.geojson')['features']
            require(len(workload)==COUNTS[phase] and len({f['id'] for f in workload})==COUNTS[phase],'Workload IDs/count differ')
            geometry_counts={kind:sum(f['geometry']['type']==kind for f in workload) for kind in ('Point','LineString','Polygon')}
            require(geometry_counts=={'Point':COUNTS[phase]*8//10,'LineString':COUNTS[phase]//10,'Polygon':COUNTS[phase]//10},'Incorrect geometry mix')
            for feature in workload:
                g=feature['geometry'];coords=g['coordinates']
                if g['type']=='Point':vertices=[coords]
                elif g['type']=='LineString':require(len(coords)==3,'Incorrect line vertex count');vertices=coords
                else:
                    require(len(coords)==1 and len(coords[0])==5 and coords[0][0]==coords[0][-1],'Incorrect polygon ring');vertices=coords[0]
                require(all(len(v)==3 and all(type(c) in (int,float) and math.isfinite(c) for c in v) for v in vertices),'Invalid workload coordinates')
        saves=[e for e in records if e['kind']=='save_requested']
        require(len(saves)==ends[0]['save_requests'],'Save request counts disagree')
        require(not saves if CONDITIONS[phase]=='idle' else bool(saves),'Missing/unexpected forced-save phase')
        require(all(e['outbox_operations']==0 for e in saves),'Unedited performance save queued work')
        if COUNTS[phase]>0 and CONDITIONS[phase]=='save':
            state=load(folder/('phase_'+str(phase))/'state.json')
            require(len(state['features'])==COUNTS[phase] and state['_ar2Sync']['pending']==[],'Final fixture save/outbox mismatch')
            require(state['features']==workload,'Unedited performance save changed feature data')
        first=group[0]
        travel=max(math.sqrt(sum((finite(r[k])-finite(first[k]))**2 for k in ('camera_x','camera_y','camera_z'))) for r in group)
        q0=[finite(first[k]) for k in ('camera_qx','camera_qy','camera_qz','camera_qw')]
        angles=[]
        for row in group:
            q=[finite(row[k]) for k in ('camera_qx','camera_qy','camera_qz','camera_qw')]
            denominator=math.sqrt(sum(x*x for x in q0)*sum(x*x for x in q))
            require(denominator>0,'Invalid camera quaternion')
            angles.append(2*math.degrees(math.acos(min(1,abs(sum(a*b for a,b in zip(q0,q)))/denominator))))
        summaries.append({'phase':phase,'extra_features':COUNTS[phase],'condition':CONDITIONS[phase],
                          'phase_elapsed_seconds':elapsed_end,'event_span_seconds':event_duration,
                          'duration_evidence':'Recorded frame phase clock; start/end events and observed frame count checked',
                          'geometry_counts':geometry_counts,'extra_coordinate_entries':COUNTS[phase]*16//10,
                          'frame_ms':distribution([finite(r['frame_ms']) for r in group]),
                          'peak_allocated_bytes':max(int(r['allocated_bytes']) for r in group),
                          'save_requests':len(saves),'save_main_thread_call_ms':distribution([finite(e['main_thread_call_ms']) for e in saves]),
                          'camera_displacement_world_units_max':travel,'camera_rotation_degrees_max':max(angles)})
    return {'result':'COMPLETE_DESCRIPTIVE_EDITOR_CAPTURE','phases':summaries,
            'not_measured':['standalone headset performance','GPU-only frame time','real HTTP latency','participant outcomes'],
            'limits':'Recorded frame intervals include Editor and collector overhead. Compare views/camera movement before pooling phases; no operational frame-budget pass is assigned.'}


def analyze(folder):
    folder=Path(folder).resolve();meta=integrity(folder)
    require(meta.get('completed') is True,'Incomplete native run; retain as failure evidence')
    require(meta.get('source_state_restored') is True,'Original scene/save state changed')
    require(meta.get('source_save_sha256_before')==meta.get('source_save_sha256_after'),'Original save-file hash changed')
    if meta.get('kind')=='height_geometry_core':result=analyze_height(folder,meta)
    elif meta.get('kind')=='editor_frame_save_scaling':result=analyze_performance(folder,meta)
    else:raise ValueError('Unsupported native capture kind')
    result['capture']=folder.name;result['unity_version']=meta['unity_version']
    return result


def pack(folder):
    folder=Path(folder).resolve();integrity(folder)
    archive=folder.with_suffix('.zip')
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as z:
        for path in sorted(folder.rglob('*')):
            if path.is_file():z.write(path,Path(folder.name)/path.relative_to(folder))
    print('Archive:',archive)
    return archive


def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__);m=p.add_mutually_exclusive_group(required=True)
    m.add_argument('--analyze-run',type=Path);m.add_argument('--pack-run',type=Path)
    args=p.parse_args(argv)
    try:
        if args.analyze_run:
            result=analyze(args.analyze_run);print(json.dumps(result,indent=2,allow_nan=False))
            if result['result']=='FAILED_NORMAL':return 1
        else:pack(args.pack_run)
        return 0
    except (OSError,ValueError,KeyError,TypeError) as e:print('FAILED:',e,file=sys.stderr);return 1


if __name__=='__main__':sys.exit(main())
