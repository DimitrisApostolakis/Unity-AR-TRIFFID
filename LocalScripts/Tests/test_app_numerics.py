"""Synthetic analyzer fixtures only: not native experiment results."""
import copy
import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('app_numerics',Path(__file__).resolve().parents[1]/'run_app_numerics.py')
app=importlib.util.module_from_spec(spec);spec.loader.exec_module(app)


def write(path,value):path.write_text(json.dumps(value),encoding='utf-8')


def seal(folder,kind='height_geometry_core'):
    meta={'schema_version':1,'kind':kind,'completed':True,'source_state_restored':True,
          'source_save_sha256_before':'test-only','source_save_sha256_after':'test-only','unity_version':'SYNTHETIC FIXTURE',
          'height_budget_m':.005,'core_result_code':0,'extra_feature_counts':list(app.COUNTS),
          'conditions':list(app.CONDITIONS),'warmup_seconds':3,'measurement_seconds_per_phase':15,
          'source_hashes_lf':{str(i):'a'*64 for i in range(3)}}
    meta['files_sha256']={str(p.relative_to(folder)):app.digest(p) for p in folder.rglob('*') if p.is_file() and p.name!='app_numerics.json'}
    write(folder/'app_numerics.json',meta)


def make_height(folder):
    rows=[]
    for profile,pose,shape,enabled,e,n,h in sorted(app.expected_height_keys()):
        hit=shape not in ('miss','inactive','no_collider')
        actual=h-4 if shape=='two_levels' else h
        rows.append({'calibration':profile,'pose':str(pose),'surface':shape,'collider_enabled':str(int(enabled)),
                     'east':str(e),'north':str(n),'expected_height_m':str(h),'expected_hit':str(int(hit)),
                     'actual_hit':str(int(hit)),'actual_height_m':str(actual) if hit else '',
                     'height_source':'mesh' if hit else 'unavailable','height_status':('below_surface' if actual<0 else 'ok') if hit else 'no_surface',
                     'collider_restored':'1','status_correct':'1','error_m':str(actual-h) if hit else '',
                     'scope':'stress' if pose==5 else 'intended_floor_diagnostic' if shape=='two_levels' else 'normal'})
    save_rows(folder/'height_raw.csv',rows)
    write(folder/'height_poses.json',[{'calibration':p,'pose':i,'matrix_column_major':[0]*16} for p in ('identity','site') for i in range(6)])
    polygons=[]
    for name,accepted in app.POLYGONS.items():
        exterior=name!='hole_clockwise'
        ring=[[23,38,100],[23.01,38,100],[23,38.01,100],[23,38,100]]
        if not exterior:ring=list(reversed(ring))
        polygons.append({'id':name,'accepted':accepted,'expected_accepted':accepted,'exterior':exterior,
                         'input':ring,'output':ring,'closure_correct':True,'winding_correct':True,
                         'heights':[100+i for i in ([0,1,2,0] if accepted else range(4))],
                         'sources':['v'+str(i) for i in ([0,1,2,0] if accepted else range(4))],
                         'statuses':['v'+str(i) for i in ([0,1,2,0] if accepted else range(4))],
                         'metadata_correct':True,'rejected_input_unchanged':True,'passed':True})
    write(folder/'polygons.json',polygons)
    (folder/'core_checks.txt').write_text('PASS synthetic check\nRESULT: 1 passed, 0 failed\n')
    seal(folder)
    return rows


def save_rows(path,rows):
    with path.open('w',newline='',encoding='utf-8') as f:
        writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)


def make_performance(folder):
    rows=[];events=[];frame=0
    for phase,(count,condition) in enumerate(zip(app.COUNTS,app.CONDITIONS)):
        features=[]
        for i in range(count):
            a,b,c,d=[23,38,100],[23.001,38,100],[23.001,38.001,100],[23,38.001,100]
            g={'type':'Point','coordinates':a}
            if i%10==8:g={'type':'LineString','coordinates':[a,b,c]}
            if i%10==9:g={'type':'Polygon','coordinates':[[a,b,c,d,a]]}
            features.append({'id':str(i),'geometry':g})
        if count:
            directory=folder/('phase_'+str(phase));directory.mkdir()
            write(directory/'fixture.geojson',{'features':features})
        events.append({'phase':phase,'kind':'phase_start','editor_seconds':phase*20,'extra_features':count,'condition':condition,'fixture_renderers':count})
        for t in (1,3,8,12,17,18.1):
            frame+=1;rows.append({'phase':str(phase),'frame':str(frame),'extra_features':str(count),'condition':condition,
                                 'phase_seconds':str(t),'warmup':str(int(t<3)),'frame_ms':'16.6','allocated_bytes':'1000','mono_used_bytes':'500',
                                 'camera_x':'0','camera_y':'0','camera_z':'0','camera_qx':'0','camera_qy':'0','camera_qz':'0','camera_qw':'1','save_in_progress':'0'})
        if condition=='save':
            events.append({'phase':phase,'kind':'save_requested','editor_seconds':phase*20+4,'main_thread_call_ms':2,'outbox_operations':0})
            directory=folder/('phase_'+str(phase))
            write(directory/'state.json',{'features':features,'_ar2Sync':{'pending':[]}})
        events.append({'phase':phase,'kind':'phase_end','editor_seconds':phase*20+18.1,'save_requests':int(condition=='save'),'observed_frames':6})
    save_rows(folder/'frames.csv',rows)
    (folder/'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events)+'\n')
    seal(folder,'editor_frame_save_scaling')
    return rows,events


class AppNumericsTests(unittest.TestCase):
    def test_full_height_coverage_keeps_wrong_floor_diagnostic_visible(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);make_height(folder);r=app.analyze(folder)
            self.assertEqual(r['result'],'PASSED_NORMAL');self.assertEqual(r['height_observations'],2160)
            self.assertEqual(r['diagnostics']['intended_floor_diagnostic']['failures'],300)
            self.assertEqual(r['polygon_fixtures'],11)

    def test_missing_and_duplicate_height_rows_are_rejected(self):
        for duplicate in (False,True):
            with tempfile.TemporaryDirectory() as d:
                folder=Path(d);rows=make_height(folder);rows=rows+[rows[0]] if duplicate else rows[:-1]
                save_rows(folder/'height_raw.csv',rows);seal(folder)
                with self.assertRaises(ValueError):app.analyze(folder)

    def test_normal_error_is_reported_as_failure_without_averaging_it_away(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);rows=make_height(folder)
            row=next(r for r in rows if r['scope']=='normal' and r['actual_hit']=='1')
            row['actual_height_m']=str(float(row['expected_height_m'])+.02);row['error_m']='.02'
            save_rows(folder/'height_raw.csv',rows);seal(folder)
            self.assertEqual(app.analyze(folder)['result'],'FAILED_NORMAL')

    def test_miss_and_collider_state_failures_are_counted(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);rows=make_height(folder)
            row=next(r for r in rows if r['scope']=='normal' and r['expected_hit']=='0');row['collider_restored']='0'
            save_rows(folder/'height_raw.csv',rows);seal(folder)
            self.assertEqual(len(app.analyze(folder)['normal_height_failures']),1)

    def test_nonfinite_measurement_and_changed_oracle_are_rejected(self):
        for key,value in [('actual_height_m','nan'),('expected_hit','0')]:
            with tempfile.TemporaryDirectory() as d:
                folder=Path(d);rows=make_height(folder);row=next(r for r in rows if r['actual_hit']=='1');row[key]=value
                save_rows(folder/'height_raw.csv',rows);seal(folder)
                with self.assertRaises(ValueError):app.analyze(folder)

    def test_tampering_or_unrestored_source_cannot_pass(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);make_height(folder);(folder/'core_checks.txt').write_text('changed')
            with self.assertRaises(ValueError):app.analyze(folder)
            make_height(folder);m=app.load(folder/'app_numerics.json');m['source_state_restored']=False;write(folder/'app_numerics.json',m)
            with self.assertRaises(ValueError):app.analyze(folder)

    def test_metadata_and_polygon_acceptance_failures_remain_failures(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);make_height(folder);cases=app.load(folder/'polygons.json')
            cases[0]['heights'][1]=999;cases[0]['metadata_correct']=False;cases[0]['passed']=False;write(folder/'polygons.json',cases);seal(folder)
            self.assertEqual(app.analyze(folder)['polygon_failures'],['ccw_exterior'])

    def test_core_failure_is_not_hidden_by_height_pass(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);make_height(folder);(folder/'core_checks.txt').write_text('FAIL synthetic\nRESULT: 0 passed, 1 failed\n');seal(folder)
            m=app.load(folder/'app_numerics.json');m['core_result_code']=1;write(folder/'app_numerics.json',m)
            self.assertEqual(app.analyze(folder)['result'],'FAILED_NORMAL')

    def test_performance_reports_seven_descriptive_editor_phases(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);make_performance(folder);r=app.analyze(folder)
            self.assertEqual(r['result'],'COMPLETE_DESCRIPTIVE_EDITOR_CAPTURE');self.assertEqual(len(r['phases']),7)
            self.assertEqual(r['phases'][-1]['extra_features'],1000)
            self.assertIn('standalone headset performance',r['not_measured'])

    def test_performance_missing_frames_or_unexpected_writes_are_rejected(self):
        for mode in ('frames','writes'):
            with tempfile.TemporaryDirectory() as d:
                folder=Path(d);rows,events=make_performance(folder)
                if mode=='frames':save_rows(folder/'frames.csv',[r for r in rows if r['phase']!='6'])
                else:
                    next(e for e in events if e['kind']=='save_requested')['outbox_operations']=1
                    (folder/'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events))
                seal(folder,'editor_frame_save_scaling')
                with self.assertRaises(ValueError):app.analyze(folder)

    def test_legacy_event_delay_does_not_shorten_recorded_phase_clock(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);rows,events=make_performance(folder)
            start=next(e for e in events if e['phase']==5 and e['kind']=='phase_start')
            start['editor_seconds']+=.101
            (folder/'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events))
            seal(folder,'editor_frame_save_scaling')
            result=app.analyze(folder)['phases'][5]
            self.assertLess(result['event_span_seconds'],18)
            self.assertGreaterEqual(result['phase_elapsed_seconds'],18)

    def test_phase_clock_requires_completion_frame_count_and_end_event(self):
        for mode in ('short','count','end','clock'):
            with tempfile.TemporaryDirectory() as d:
                folder=Path(d);rows,events=make_performance(folder)
                end=next(e for e in events if e['phase']==5 and e['kind']=='phase_end')
                if mode=='short':
                    next(r for r in reversed(rows) if r['phase']=='5')['phase_seconds']='17.999'
                elif mode=='count':end['observed_frames']-=1
                elif mode=='end':events.remove(end)
                else:
                    next(e for e in events if e['phase']==5 and e['kind']=='phase_start')['phase_clock_start_seconds']=101
                save_rows(folder/'frames.csv',rows)
                (folder/'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events))
                seal(folder,'editor_frame_save_scaling')
                with self.assertRaises(ValueError):app.analyze(folder)

    def test_explicit_phase_clock_is_accepted(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d);rows,events=make_performance(folder)
            for event in events:
                if event['kind']=='phase_start':event['phase_clock_start_seconds']=event['editor_seconds']
            (folder/'events.jsonl').write_text('\n'.join(json.dumps(e) for e in events))
            seal(folder,'editor_frame_save_scaling')
            self.assertEqual(app.analyze(folder)['result'],'COMPLETE_DESCRIPTIVE_EDITOR_CAPTURE')

    def test_nested_evidence_is_hashed_and_archives_never_overwrite(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d)/'capture';folder.mkdir();make_performance(folder)
            archive=app.pack(folder);self.assertTrue(archive.is_file())
            with self.assertRaises(FileExistsError):app.pack(folder)
            (folder/'phase_6/state.json').write_text('{}')
            with self.assertRaises(ValueError):app.analyze(folder)


if __name__=='__main__':unittest.main()
