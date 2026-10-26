#!/usr/bin/env python3
"""Prepare/analyze E2 native scene manipulation and unedited persistence measurements."""
from __future__ import annotations
import argparse
from collections import Counter
import csv
import json
import math
from pathlib import Path
import shutil
import sys
import zipfile
from datetime import datetime, timezone
import run_numerical_roundtrip as numerical

REPO = numerical.REPO
EDITOR = REPO / 'projects/TRIFFID/Assets/TRIFFID/Editor/Experiments'
SOURCE_PATHS = [*sorted((numerical.RUNTIME / 'Features/Spawning').glob('JsonSpawner*.cs')),
    numerical.RUNTIME / 'Geospatial/Coordinates/GeoUtils.cs',
    numerical.RUNTIME / 'Features/Model/GeoJsonDataModels.cs',
    numerical.RUNTIME / 'Features/Model/GeoJsonPolygonValidator.cs',
    numerical.RUNTIME / 'Features/Model/GeoJsonVertexEdit.cs',
    numerical.RUNTIME / 'Features/Persistence/GeoJsonSyncState.cs',
    numerical.RUNTIME / 'Features/Persistence/GeoJsonAtomicFile.cs',
    numerical.RUNTIME / 'Features/Presentation/FloatingIcon.cs',
    numerical.RUNTIME / 'Features/Presentation/PointData.cs',
    EDITOR / 'SceneConsistencyCollector.cs', EDITOR / 'SceneConsistencySession.cs']
EXPECTED_HEADER = ['feature_id', 'vertex_path', 'geometry_type', 'source_sample_id', 'lon', 'lat', 'height']
RAW_HEADER = ['phase', 'condition', 'scope', 'step', 'cycle', 'feature_id', 'vertex_path', 'geometry_type',
    'local_x', 'local_y', 'local_z', 'world_x', 'world_y', 'world_z', 'inverse_x', 'inverse_y', 'inverse_z',
    'lon', 'lat', 'height', 'stored_lon', 'stored_lat', 'stored_height', 'baseline_lon', 'baseline_lat', 'baseline_height']


def select_locations(rows, count=30):
    """Deterministic farthest-point coverage; exact duplicate OBJ positions count once."""
    unique = {}
    for row in rows:
        if row['group'] != 'mesh_vertices': continue
        xyz = tuple(float(row[k]) for k in ('cx', 'cy', 'cz'))
        if not all(math.isfinite(v) for v in xyz): raise ValueError('Nonfinite mesh input')
        unique.setdefault(xyz, row)
    points = list(unique)
    if len(points) < count: raise ValueError(f'Need at least {count} distinct mesh positions')
    # Stable lexicographic starting boundary, followed by maximum distance to selected set.
    chosen = [min(range(len(points)), key=lambda i: points[i])]
    distance = [math.inf] * len(points)
    for _ in range(count - 1):
        last = points[chosen[-1]]
        distance = [min(d, sum((a-b)**2 for a,b in zip(p,last))) for p,d in zip(points,distance)]
        chosen.append(max((i for i in range(len(points)) if i not in chosen), key=lambda i: (distance[i], -i)))
    return [unique[points[i]] for i in chosen]


def convex_hull(rows):
    ordered = sorted(rows, key=lambda r: (float(r['lon']), float(r['lat'])))
    def cross(a,b,c):
        return (float(b['lon'])-float(a['lon']))*(float(c['lat'])-float(a['lat'])) - (float(b['lat'])-float(a['lat']))*(float(c['lon'])-float(a['lon']))
    halves = []
    for sequence in (ordered, list(reversed(ordered))):
        half = []
        for row in sequence:
            while len(half) >= 2 and cross(half[-2], half[-1], row) <= 0: half.pop()
            half.append(row)
        halves.append(half[:-1])
    return halves[0] + halves[1]


def fixture(selected):
    hull = convex_hull(selected)
    if len(hull) < 4: raise ValueError('Need four non-collinear hull points for the polygon fixture')
    polygon = [hull[len(hull)*i//4] for i in range(4)]
    other = [r for r in selected if r not in polygon]
    features, expected = [], []
    def feature(fid, kind, points, class_name):
        coords = [[float(r[k]) for k in ('lon','lat','height')] for r in points]
        if not all(math.isfinite(v) for p in coords for v in p): raise ValueError('Nonfinite geographic fixture')
        if kind == 'Point': coordinates, paths = coords[0], ['']
        elif kind == 'LineString': coordinates, paths = coords, [str(i) for i in range(len(coords))]
        else:
            points = [*points, points[0]]
            coordinates, paths = [[*coords, list(coords[0])]], ['0/'+str(i) for i in range(len(points))]
        features.append({'type':'Feature','id':fid,'properties':{'id':fid,'class':class_name,
            'category':'experiment','source':'ground station','marker-color':'#ff0000'},
            'geometry':{'type':kind,'coordinates':coordinates}})
        for path, row in zip(paths, points):
            expected.append([fid,path,kind,row['id'],*[float(row[k]) for k in ('lon','lat','height')]])
    for i,row in enumerate(other[:20]): feature(f'e2_point_{i:02}', 'Point', [row], 'animal')
    feature('e2_line', 'LineString', other[20:], 'road')
    feature('e2_polygon', 'Polygon', polygon, 'safe')
    return {'type':'FeatureCollection','features':features}, expected


def plan():
    poses = []
    def add(name, scope='normal', xyz=None, rotation=None, units=None, captured=False):
        poses.append({'id':name,'scope':scope,'position_world':xyz,'rotation_euler_local_delta':rotation or [0,0,0],
                      'world_units_per_ground_metre_up':units,'captured_pose':captured})
    add('captured', captured=True)
    for i, xyz in enumerate([[0,0,0],[2,1,-3],[-4,2,5]]): add(f'translation_{i}',xyz=xyz)
    add('translation_return',captured=True)
    for i, rotation in enumerate([[0,45,0],[30,120,-15],[-60,240,35]]): add(f'rotation_{i}',rotation=rotation)
    add('rotation_return',captured=True)
    for units in [.001,.01,1.0]: add(f'scale_{units:g}',xyz=[0,0,0],units=units)
    add('scale_return',captured=True)
    for i in range(20): add(f'combined_{i:02}',xyz=[3,-1,2] if i%2 else [-2,1,-3],rotation=[23*i%75,57*i%360,-11*i%65],units=.001 if i%2 else 1.)
    add('combined_return',captured=True)
    for offset in [100,1000,10000]:
        for units in [.001,.01,1.0]: add(f'offset_{offset}_scale_{units:g}',scope='stress',xyz=[offset,offset,-offset],rotation=[23,57,-11],units=units)
    add('final_return',captured=True)
    trials = [dict(poses[0]), {'id':'tabletop_rotated','scope':'normal','position_world':[3,1.5,-2],
        'rotation_euler_local_delta':[23,57,-11],'world_units_per_ground_metre_up':.001,'captured_pose':False},
        {'id':'full_metric_up_rotated','scope':'normal','position_world':[0,0,0],
        'rotation_euler_local_delta':[23,57,-11],'world_units_per_ground_metre_up':1.,'captured_pose':False},
        {'id':'tabletop_offset_10000','scope':'stress','position_world':[10000,10000,-10000],
        'rotation_euler_local_delta':[23,57,-11],'world_units_per_ground_metre_up':.001,'captured_pose':False}]
    return {'schema_version':1,'poses':poses,'persistence_trials':trials,'warmup_save_reload_cycles':1,
        'checkpoint_cycles':[0,1,10,100,1000],
        'scale_definition':'Normalize the ENU Up axis to the specified world units per ground metre. Record East/North/Up lengths; full metric similarity requires all three equal 1.',
        'scope_note':'Normal/stress labels are provisional pilot conditions, not a validated operating envelope.'}


def prepare(source, output, threshold=.01):
    if not math.isfinite(threshold) or threshold <= 0: raise ValueError('Use a finite positive numerical threshold')
    manifest = json.loads((source/'prepared.json').read_text())
    for name,key in [('alignment.json','alignment_sha256'),('samples.csv','samples_sha256')]:
        if numerical.digest(source/name) != manifest[key]: raise ValueError('E1 input hash mismatch: '+name)
    numerical.calibration(json.loads((source/'alignment.json').read_text()), True)
    with (source/'samples.csv').open(newline='') as stream: selected = select_locations(list(csv.DictReader(stream)))
    root, expected = fixture(selected)
    sources = {str(p.relative_to(REPO/'projects/TRIFFID/Assets')):numerical.source_digest(p) for p in SOURCE_PATHS}
    output.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(source/'alignment.json',output/'alignment.json')
    numerical.dump(output/'fixture.geojson',root); numerical.dump(output/'plan.json',plan())
    with (output/'expected.csv').open('w',newline='') as stream:
        w=csv.writer(stream);w.writerow(EXPECTED_HEADER);w.writerows(expected)
    numerical.dump(output/'prepared_scene.json',{'schema_version':1,'created_utc':datetime.now(timezone.utc).isoformat(),
        'stage':'pilot_scene_consistency','threshold_m':threshold,'threshold_status':'provisional engineering target; not mission accuracy',
        'distinct_locations':len(selected),'coordinate_entries':len(expected),'features':len(root['features']),
        'sampling':'30 distinct OBJ vertices; deterministic farthest-point coverage; 20 POIs, 6 line vertices, 4 polygon vertices plus duplicate ring closure.',
        'e1_samples_sha256':manifest['samples_sha256'],'files_sha256':{n:numerical.digest(output/n) for n in ['alignment.json','fixture.geojson','plan.json','expected.csv']},
        'sources_lf_sha256':sources,'analysis_driver_sha256':numerical.digest(Path(__file__))})
    print(f'Prepared {len(selected)} distinct locations, {len(expected)} coordinate entries in {output}')


def repeat_from_capture(source, output):
    """Keep the exact fixture, plan and tolerance, refreshing only code provenance."""
    prepared=json.loads((source/'prepared_scene.json').read_text())
    runtime=json.loads((source/'scene_runtime.json').read_text())
    if prepared.get('schema_version')!=1 or not runtime.get('completed') or not runtime.get('source_state_restored'):
        raise ValueError('Use a completed capture with restored source state as the comparison baseline')
    for name,expected in prepared['files_sha256'].items():
        if numerical.digest(source/name)!=expected: raise ValueError('Baseline input changed: '+name)
    for name,expected in runtime['output_sha256'].items():
        if numerical.digest(source/name)!=expected: raise ValueError('Baseline output changed: '+name)
    output.mkdir(parents=True,exist_ok=False)
    for name in ['alignment.json','fixture.geojson','plan.json','expected.csv']:
        shutil.copyfile(source/name,output/name)
    prepared['created_utc']=datetime.now(timezone.utc).isoformat()
    prepared['sources_lf_sha256']={str(p.relative_to(REPO/'projects/TRIFFID/Assets')):numerical.source_digest(p) for p in SOURCE_PATHS}
    prepared['analysis_driver_sha256']=numerical.digest(Path(__file__))
    prepared['comparison_baseline']={'capture':source.name,
        'prepared_sha256':numerical.digest(source/'prepared_scene.json'),
        'runtime_sha256':numerical.digest(source/'scene_runtime.json'),
        'output_sha256':runtime['output_sha256'], 'max_cycles':runtime['max_cycles'],
        'note':'Original failed capture retained unchanged; these are inputs for a NEW native run, not new measurements.'}
    prepared['require_exact_stored_coordinates']=True
    prepared['require_explicit_edit_checks']=True
    numerical.dump(output/'prepared_scene.json',prepared)
    print('Prepared exact-input repeat in '+str(output))


def analyze(directory, make_plot=True):
    runtime=json.loads((directory/'scene_runtime.json').read_text())
    prepared=json.loads((directory/'prepared_scene.json').read_text())
    if runtime.get('schema_version')!=1 or prepared['schema_version']!=1: raise ValueError('Unsupported scene measurement version')
    if not runtime.get('completed') or not runtime.get('source_state_restored'): raise ValueError('Incomplete run or source restoration failed; retain as failure evidence')
    for name,digest in prepared['files_sha256'].items():
        if numerical.digest(directory/name)!=digest: raise ValueError('Prepared input hash mismatch: '+name)
    for name,digest in runtime['output_sha256'].items():
        if numerical.digest(directory/name)!=digest: raise ValueError('Collected output hash mismatch: '+name)
    if runtime['source_hashes_lf']!=prepared['sources_lf_sha256']: raise ValueError('Measured source identity mismatch')
    with (directory/'expected.csv').open(newline='') as stream:
        rows=csv.DictReader(stream)
        if rows.fieldnames!=EXPECTED_HEADER: raise ValueError('Expected schema mismatch')
        expected={(r['feature_id'],r['vertex_path']):r for r in rows}
    if len(expected)!=prepared['coordinate_entries']: raise ValueError('Expected coordinate identity/count mismatch')
    cfg=numerical.calibration(json.loads((directory/'alignment.json').read_text()), True)
    if 'calibration_float' in runtime and numerical.calibration(runtime['calibration_float'],True)[:4]!=cfg[:4]:
        raise ValueError('Runtime float calibration mismatch')
    ref=numerical.Reference(cfg[0]); series={};attempted={};previous={};world_baselines={};seen=set();total=0;stored_mismatches=Counter()
    if runtime['max_cycles'] not in [100,1000]: raise ValueError('Unexpected cycle limit')
    plan_data=json.loads((directory/'plan.json').read_text())
    scopes={('pose',p['id']):p['scope'] for p in plan_data['poses']}
    for p in plan_data['persistence_trials']:
        for phase in ['cycle_initial','cycle_warmup','cycle']: scopes[(phase,p['id'])]=p['scope']
    scopes.update({('initial_load','manipulation'):'normal',('warmup','manipulation'):'normal'})
    with (directory/'scene_raw.csv').open(newline='') as stream, (directory/'scene_errors.csv').open('w',newline='') as output:
        rows=csv.DictReader(stream); w=csv.writer(output)
        if rows.fieldnames!=RAW_HEADER: raise ValueError('Raw schema mismatch')
        w.writerow(['phase','condition','scope','step','cycle','feature_id','vertex_path','geometry_type','measurement','status','dE_m','dN_m','dU_m','error3d_m'])
        for row in rows:
            total+=1; identity=(row['feature_id'],row['vertex_path']); target=expected.get(identity)
            if scopes.get((row['phase'],row['condition']))!=row['scope']: raise ValueError('Unexpected condition/scope classification')
            if target is None or row['geometry_type']!=target['geometry_type']: raise ValueError('Unexpected feature/vertex identity')
            event=(row['phase'],row['condition'],int(row['step']),int(row['cycle']),*identity)
            if event in seen: raise ValueError('Duplicate coordinate measurement')
            seen.add(event)
            actual=tuple(float(row[k]) for k in ['lon','lat','height']); baseline=tuple(float(row['baseline_'+k]) for k in ['lon','lat','height'])
            stored=tuple(float(row['stored_'+k]) for k in ['lon','lat','height']); original=tuple(float(target[k]) for k in ['lon','lat','height'])
            if stored!=original: stored_mismatches[row['scope']]+=1
            local=tuple(float(row['local_'+k]) for k in 'xyz'); inverse=tuple(float(row['inverse_'+k]) for k in 'xyz')
            world=tuple(float(row['world_'+k]) for k in 'xyz')
            finite=all(math.isfinite(v) for v in (*actual,*baseline,*stored,*local,*inverse,*world))
            measures={k:None for k in ['scene_vs_input','scene_vs_baseline','stored_vs_input','world_vs_input','world_vs_baseline','inverse_vs_production']}
            context='manipulation' if row['phase'] in ['initial_load','warmup','pose'] else row['condition']
            world_key=(context,*identity)
            world_enu=numerical.enu_position(inverse,cfg) if finite else None
            if row['phase'] in ['initial_load','cycle_initial'] or row['phase']=='cycle' and int(row['cycle'])==0:
                world_baselines[world_key]=world_enu
            if finite:
                measures.update(scene_vs_input=ref.geographic_delta(actual,original),scene_vs_baseline=ref.geographic_delta(actual,baseline),
                    stored_vs_input=ref.geographic_delta(stored,original),world_vs_input=numerical.sub(world_enu,ref.to_enu(original)),
                    world_vs_baseline=numerical.sub(world_enu,world_baselines[world_key]) if world_baselines.get(world_key) is not None else None,
                    inverse_vs_production=tuple(cfg[1]*v for v in numerical.matvec(cfg[2],numerical.sub(inverse,local))))
            if row['phase']=='warmup': world_baselines[world_key]=world_enu
            key=(row['condition'],*identity)
            if row['phase']=='cycle':
                cycle=int(row['cycle'])
                if cycle==0: previous[key]=(cycle,actual)
                else:
                    if key not in previous or previous[key][0]!=cycle-1: raise ValueError('Missing/out-of-order cycle increment')
                    prev=previous[key][1]; previous[key]=(cycle,actual)
                    measures['cycle_increment']=ref.geographic_delta(actual,prev) if finite and all(math.isfinite(x) for x in prev) else None
            for measurement,delta in measures.items():
                good=delta is not None and all(math.isfinite(x) for x in delta)
                group=(row['phase'],row['scope'],row['condition'],row['geometry_type'],measurement)
                attempted[group]=attempted.get(group,0)+1;series.setdefault(group,[])
                if good:series[group].append(delta)
                w.writerow([*[row[k] for k in ['phase','condition','scope','step','cycle','feature_id','vertex_path','geometry_type']],measurement,'ok' if good else 'nonfinite',
                    *delta,math.sqrt(sum(x*x for x in delta))] if good else [*[row[k] for k in ['phase','condition','scope','step','cycle','feature_id','vertex_path','geometry_type']],measurement,'nonfinite','','','',''])
    # Every planned event must contain every expected vertex, including explicit ring closure.
    plan_data=json.loads((directory/'plan.json').read_text())
    scheduled={('initial_load','manipulation',0,0),('warmup','manipulation',0,0)}
    scheduled.update(('pose',p['id'],i,0) for i,p in enumerate(plan_data['poses']))
    for i,p in enumerate(plan_data['persistence_trials']):
        scheduled.update({('cycle_initial',p['id'],i,0),('cycle_warmup',p['id'],i,0)})
        scheduled.update(('cycle',p['id'],i,c) for c in range(runtime['max_cycles']+1))
    expected_events={(*event,*identity) for event in scheduled for identity in expected}
    if seen!=expected_events or total!=runtime['raw_rows']: raise ValueError('Planned observations are missing or unexpected')
    summaries=[dict(phase=p,scope=s,condition=c,geometry_type=g,measurement=m,**numerical.summarize(values,attempted[(p,s,c,g,m)],prepared['threshold_m'])) for (p,s,c,g,m),values in series.items()]
    with (directory/'save_events.csv').open(newline='') as stream: saves=list(csv.DictReader(stream))
    expected_saves={('warmup','manipulation',0),*{('pose',p['id'],0) for p in plan_data['poses']}}
    for p in plan_data['persistence_trials']:
        expected_saves.add(('cycle_warmup',p['id'],0))
        expected_saves.update(('cycle',p['id'],c) for c in range(1,runtime['max_cycles']+1))
    if {(r['phase'],r['condition'],int(r['cycle'])) for r in saves}!=expected_saves or len(saves)!=len(expected_saves):raise ValueError('Missing/duplicate save events')
    for row in saves:
        if scopes.get((row['phase'],row['condition']))!=row['scope'] or any(int(row[k])<0 for k in ['cycle','new_operations','pending_operations','changed_feature_semantics','next_revision']): raise ValueError('Malformed save event')
    with (directory/'outbox_events.jsonl').open() as stream: operations=[json.loads(line) for line in stream if line.strip()]
    operation_counts=Counter((x['phase'],x['condition'],int(x['cycle'])) for x in operations)
    for row in saves:
        count=operation_counts[(row['phase'],row['condition'],int(row['cycle']))]
        if count!=int(row['new_operations']): raise ValueError('Outbox operation payload/count mismatch')
    if len(operations)!=sum(int(r['new_operations']) for r in saves): raise ValueError('Unexpected outbox operations')
    queue={scope:sum(int(r['new_operations']) for r in saves if r['scope']==scope and r['phase'] in ['pose','cycle']) for scope in ['normal','stress']}
    normal_primary=[r for r in summaries if r['scope']=='normal' and r['phase'] in ['pose','cycle'] and r['measurement'] in ['scene_vs_input','scene_vs_baseline','stored_vs_input','world_vs_input','world_vs_baseline']]
    result={'stage':'pilot_scene_consistency','profile':runtime['profile'],'raw_rows':total,'threshold_m':prepared['threshold_m'],
        'summary':summaries,'new_outbox_operations_after_warmup':queue,
        'exact_stored_coordinate_mismatches':dict(stored_mismatches),
        'actual_network_uploads':'not measured: sandbox has no sync coordinator or API manager',
        'source_state_restored':True,'analysis_environment':{'python':sys.version,'pyproj':ref.version,'proj':ref.proj_version},
        'normal_pass':bool(normal_primary) and all(r['pass'] for r in normal_primary) and queue['normal']==0}
    if prepared.get('require_exact_stored_coordinates'):
        result['stored_coordinates_pass']=not stored_mismatches
        result['unedited_saves_pass']=all(int(r['new_operations'])==0 and int(r['changed_feature_semantics'])==0 for r in saves)
        result['normal_pass'] &= result['stored_coordinates_pass'] and result['unedited_saves_pass']
    if prepared.get('require_explicit_edit_checks'):
        if 'deliberate_edit_checks.json' not in runtime['output_sha256']:
            raise ValueError('Native deliberate edit evidence is missing')
        checks=json.loads((directory/'deliberate_edit_checks.json').read_text())
        result['deliberate_edits_pass']=runtime.get('deliberate_edits_passed') is True and checks.get('passed') is True and \
            [c.get('geometry') for c in checks.get('checks',[])]==['Point','LineString','Polygon'] and \
            all(c.get('saved_and_reloaded') is True and c.get('repeat_save_new_operations')==0 for c in checks['checks'])
        result['normal_pass'] &= result['deliberate_edits_pass']
    numerical.dump(directory/'scene_summary.json',result)
    with (directory/'scene_summary.csv').open('w',newline='') as stream:
        w=csv.writer(stream); w.writerow(['phase','scope','condition','geometry_type','measurement','attempted','failed','max_3d_m','p95_3d_m','exceeding','pass'])
        for r in summaries:w.writerow([*[r[k] for k in ['phase','scope','condition','geometry_type','measurement','attempted','failed']],r.get('3d',{}).get('max_m'),r.get('3d',{}).get('p95_m'),r.get('exceeding_3d_threshold'),r['pass']])
    if make_plot:
        curves={}
        for k,values in series.items():
            if k[0] in ['pose','cycle'] and k[4]=='scene_vs_baseline': curves.setdefault((k[0]+'/'+k[1],'ALL'),[]).extend(values)
        numerical.plot(directory,curves,prepared['threshold_m'])
    print(json.dumps({'normal_pass':result['normal_pass'],'raw_rows':total,'new_outbox_operations_after_warmup':queue,'actual_network_uploads':result['actual_network_uploads']},indent=2))
    return result


def pack(directory):
    """Pack unchanged capture evidence, including an incomplete run for diagnosis."""
    runtime=json.loads((directory/'scene_runtime.json').read_text())
    for name,expected in runtime['output_sha256'].items():
        if numerical.digest(directory/name)!=expected: raise ValueError('Collected output hash mismatch: '+name)
    archive=directory.parent/(directory.name+'.zip')
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as z:
        for file in sorted(directory.rglob('*')):
            if file.is_file():z.write(file,Path(directory.name)/file.relative_to(directory))
    print('Capture archive: '+str(archive))
    return archive


def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--prepare-from',type=Path);p.add_argument('--output',type=Path);p.add_argument('--threshold-m',type=float,default=.01);p.add_argument('--analyze-run',type=Path);p.add_argument('--pack-run',type=Path)
    p.add_argument('--repeat-from-capture',type=Path)
    a=p.parse_args(argv)
    try:
        if a.pack_run:pack(a.pack_run.resolve());return 0
        if a.analyze_run:return 0 if analyze(a.analyze_run.resolve())['normal_pass'] else 1
        if a.repeat_from_capture:
            if not a.output:p.error('Use --repeat-from-capture BASELINE_CAPTURE --output NEW_FOLDER')
            repeat_from_capture(a.repeat_from_capture.resolve(),a.output.resolve());return 0
        if not a.prepare_from or not a.output:p.error('Use --prepare-from E1_FOLDER --output NEW_FOLDER, or --analyze-run CAPTURE_FOLDER')
        if not math.isfinite(a.threshold_m) or a.threshold_m<=0:p.error('threshold must be finite and positive')
        prepare(a.prepare_from.resolve(),a.output.resolve(),a.threshold_m);return 0
    except (OSError,ValueError,KeyError,ImportError,csv.Error) as error:
        print('FAILED: '+str(error),file=sys.stderr);return 2

if __name__=='__main__':raise SystemExit(main())

