"""Measurement oracle/coverage checks. Generated test CSVs are not experimental data."""
import csv
import importlib.util
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest

SCRIPTS=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(SCRIPTS))
import run_scene_consistency as scene
import run_numerical_roundtrip as numerical

CONFIG={'origin_wgs84':{'lon':23.7,'lat':37.9,'alt':100},'colmap_to_enu':{'scale':2.5,'R_rowmajor':[1,0,0,0,1,0,0,0,1],'t':[0,0,0]}}

def mesh_rows():
    return [dict(id=str(i),group='mesh_vertices',cx=str(x),cy=str(y),cz=str(z),
        lon=str(23.7+x/100000),lat=str(37.9+y/100000),height=str(100+z))
        for i,(x,y,z) in enumerate((x,y,z) for x in range(6) for y in range(6) for z in range(3))]

def prepare_test_folder(folder):
    source=folder/'e1';source.mkdir()
    numerical.dump(source/'alignment.json',CONFIG)
    with (source/'samples.csv').open('w',newline='') as stream:
        w=csv.DictWriter(stream,fieldnames=numerical.SAMPLE_HEADER);w.writeheader();w.writerows(mesh_rows())
    numerical.dump(source/'prepared.json',{'alignment_sha256':numerical.digest(source/'alignment.json'),'samples_sha256':numerical.digest(source/'samples.csv')})
    output=folder/'run';scene.prepare(source,output)
    return source,output

def fabricated_capture(directory):
    """Oracle fixture: exact returns in all planned events, independent of engine arithmetic."""
    prepared=json.loads((directory/'prepared_scene.json').read_text())
    plan=json.loads((directory/'plan.json').read_text())
    with (directory/'expected.csv').open(newline='') as stream: expected=list(csv.DictReader(stream))
    reference=numerical.Reference(numerical.calibration(CONFIG)[0])
    for row in expected:row['local_oracle']=tuple(x/CONFIG['colmap_to_enu']['scale'] for x in reference.to_enu(tuple(float(row[k]) for k in ['lon','lat','height'])))
    count=0; saves=[]
    def event(phase,condition,scope,step,cycle):
        for p in expected:
            row=dict.fromkeys(scene.RAW_HEADER,'0')
            row.update(phase=phase,condition=condition,scope=scope,step=str(step),cycle=str(cycle),feature_id=p['feature_id'],vertex_path=p['vertex_path'],geometry_type=p['geometry_type'])
            for k in ['lon','lat','height']:
                row[k]=row['stored_'+k]=row['baseline_'+k]=p[k]
            for axis,value in zip('xyz',p['local_oracle']):row['local_'+axis]=row['inverse_'+axis]=str(value)
            yield row
    def save(phase,condition,scope,cycle):
        saves.append(dict(phase=phase,condition=condition,scope=scope,cycle=cycle,new_operations=0,pending_operations=0,changed_feature_semantics=0,next_revision=1))
    with (directory/'scene_raw.csv').open('w',newline='') as stream:
        writer=csv.DictWriter(stream,fieldnames=scene.RAW_HEADER);writer.writeheader()
        def rows(phase,condition,scope,step,cycle):
            nonlocal count
            data=list(event(phase,condition,scope,step,cycle));writer.writerows(data);count+=len(data)
        rows('initial_load','manipulation','normal',0,0);rows('warmup','manipulation','normal',0,0)
        save('warmup','manipulation','normal',0)
        for i,p in enumerate(plan['poses']):
            rows('pose',p['id'],p['scope'],i,0);save('pose',p['id'],p['scope'],0)
        for i,p in enumerate(plan['persistence_trials']):
            rows('cycle_initial',p['id'],p['scope'],i,0);rows('cycle_warmup',p['id'],p['scope'],i,0)
            save('cycle_warmup',p['id'],p['scope'],0)
            for cycle in range(101):
                rows('cycle',p['id'],p['scope'],i,cycle)
                if cycle:save('cycle',p['id'],p['scope'],cycle)
    with (directory/'save_events.csv').open('w',newline='') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(saves[0]));writer.writeheader();writer.writerows(saves)
    (directory/'outbox_events.jsonl').write_text('')
    runtime={'schema_version':1,'completed':True,'source_state_restored':True,'profile':'fabricated unit-test oracle; not Unity',
        'max_cycles':100,'raw_rows':count,'source_hashes_lf':prepared['sources_lf_sha256'],'output_sha256':{}}
    numerical.dump(directory/'scene_runtime.json',runtime);rehash(directory)

def rehash(directory):
    runtime=json.loads((directory/'scene_runtime.json').read_text())
    runtime['output_sha256']={n:numerical.digest(directory/n) for n in ['scene_raw.csv','save_events.csv','outbox_events.jsonl']}
    numerical.dump(directory/'scene_runtime.json',runtime)

def edit_raw(directory, mutate):
    with (directory/'scene_raw.csv').open(newline='') as stream:rows=list(csv.DictReader(stream))
    mutate(rows)
    with (directory/'scene_raw.csv').open('w',newline='') as stream:
        w=csv.DictWriter(stream,fieldnames=scene.RAW_HEADER);w.writeheader();w.writerows(rows)

class SceneTests(unittest.TestCase):
    def test_repeat_keeps_exact_inputs_and_threshold_and_updates_source_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            _,baseline=prepare_test_folder(Path(temporary))
            numerical.dump(baseline/'scene_runtime.json',{'schema_version':1,'completed':True,'source_state_restored':True,'max_cycles':100,'output_sha256':{}})
            before={p.name:p.read_bytes() for p in baseline.iterdir() if p.is_file()}
            repeat=Path(temporary)/'repeat';scene.repeat_from_capture(baseline,repeat)
            for name in ['alignment.json','fixture.geojson','plan.json','expected.csv']:
                self.assertEqual((repeat/name).read_bytes(),before[name])
            old=json.loads(before['prepared_scene.json']);new=json.loads((repeat/'prepared_scene.json').read_text())
            self.assertEqual(new['threshold_m'],old['threshold_m'])
            self.assertTrue(new['require_exact_stored_coordinates']);self.assertTrue(new['require_explicit_edit_checks'])
            self.assertEqual(new['comparison_baseline']['prepared_sha256'],numerical.digest(baseline/'prepared_scene.json'))
            self.assertEqual(before,{p.name:p.read_bytes() for p in baseline.iterdir() if p.is_file()})
            with self.assertRaises(FileExistsError):scene.repeat_from_capture(baseline,repeat)
            (baseline/'fixture.geojson').write_text('{}')
            with self.assertRaises(ValueError):scene.repeat_from_capture(baseline,Path(temporary)/'corrupt')

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_exact_storage_check_rejects_sub_tolerance_drift(self):
        with tempfile.TemporaryDirectory() as temporary:
            _,capture=prepare_test_folder(Path(temporary));fabricated_capture(capture)
            manifest=json.loads((capture/'prepared_scene.json').read_text());manifest['require_exact_stored_coordinates']=True
            numerical.dump(capture/'prepared_scene.json',manifest)
            def change(rows):
                row=next(r for r in rows if r['phase']=='cycle' and r['cycle']=='100' and r['scope']=='normal')
                row['stored_height']=str(float(row['stored_height'])+0.000001)
            edit_raw(capture,change);rehash(capture)
            result=scene.analyze(capture,make_plot=False)
            self.assertFalse(result['normal_pass']);self.assertFalse(result['stored_coordinates_pass'])
            self.assertEqual(result['exact_stored_coordinate_mismatches']['normal'],1)

    def test_selection_spans_boundaries_and_is_deterministic_without_duplicates(self):
        rows=mesh_rows();selected=scene.select_locations(rows+rows)
        self.assertEqual(selected,scene.select_locations(rows));self.assertEqual(len({tuple(r[k] for k in ['cx','cy','cz']) for r in selected}),30)
        for k in ['cx','cy','cz']:
            self.assertEqual(min(float(r[k]) for r in selected),min(float(r[k]) for r in rows))
            self.assertEqual(max(float(r[k]) for r in selected),max(float(r[k]) for r in rows))
        with self.assertRaises(ValueError):scene.select_locations(rows[:4])

    def test_fixture_has_real_geometry_roles_and_a_single_counted_ring_closure(self):
        root,expected=scene.fixture(scene.select_locations(mesh_rows()))
        self.assertEqual(len(root['features']),22);self.assertEqual(len(expected),31)
        self.assertEqual(sum(r[2]=='Point' for r in expected),20)
        self.assertEqual(sum(r[2]=='LineString' for r in expected),6)
        ring=root['features'][-1]['geometry']['coordinates'][0]
        self.assertEqual(ring[0],ring[-1]);self.assertEqual(len(ring),5)
        self.assertEqual(len({tuple(p) for p in ring}),4)
        area=sum(a[0]*b[1]-b[0]*a[1] for a,b in zip(ring,ring[1:]))
        self.assertGreater(area,0)

    def test_preparation_is_immutable_and_checks_e1_hashes(self):
        with tempfile.TemporaryDirectory() as tmp:
            source,output=prepare_test_folder(Path(tmp))
            with self.assertRaises(FileExistsError):scene.prepare(source,output)
            (source/'samples.csv').write_text('corrupt')
            with self.assertRaises(ValueError):scene.prepare(source,Path(tmp)/'different')

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_complete_oracle_capture_passes_and_raw_tampering_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            _,run=prepare_test_folder(Path(tmp));fabricated_capture(run)
            result=scene.analyze(run,False)
            self.assertTrue(result['normal_pass']);self.assertEqual(result['new_outbox_operations_after_warmup']['normal'],0)
            self.assertIn('not measured',result['actual_network_uploads'])
            with (run/'scene_raw.csv').open('a') as stream:stream.write('tamper')
            with self.assertRaises(ValueError):scene.analyze(run,False)

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_missing_observation_and_wrong_scope_cannot_pass(self):
        for fault in ['missing','scope']:
            with self.subTest(fault=fault),tempfile.TemporaryDirectory() as tmp:
                _,run=prepare_test_folder(Path(tmp));fabricated_capture(run)
                def mutate(rows):
                    if fault=='missing':rows.pop()
                    else:rows[-1]['scope']='normal'
                edit_raw(run,mutate);rehash(run)
                with self.assertRaises(ValueError):scene.analyze(run,False)

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_collected_nonfinite_and_threshold_exceedance_are_failures(self):
        for value in ['NaN','1000','world_only']:
            with self.subTest(value=value),tempfile.TemporaryDirectory() as tmp:
                _,run=prepare_test_folder(Path(tmp));fabricated_capture(run)
                def mutate(rows):
                    row=next(r for r in rows if r['phase']=='cycle' and r['scope']=='normal' and r['cycle']=='1')
                    if value=='world_only':row['inverse_x']=str(float(row['inverse_x'])+1)
                    else:row['height']=value
                edit_raw(run,mutate);rehash(run) # Explicitly simulate a legitimate failing collector output.
                result=scene.analyze(run,False)
                self.assertFalse(result['normal_pass'])
                if value=='NaN':self.assertTrue(any(r['failed']>0 for r in result['summary']))
                else:self.assertTrue(any(r.get('exceeding_3d_threshold',0)>0 for r in result['summary']))

    def test_incomplete_or_unrestored_capture_is_rejected(self):
        for field in ['completed','source_state_restored']:
            with self.subTest(field=field),tempfile.TemporaryDirectory() as tmp:
                run=Path(tmp);numerical.dump(run/'prepared_scene.json',{'schema_version':1})
                numerical.dump(run/'scene_runtime.json',{'schema_version':1,'completed':field!='completed','source_state_restored':field!='source_state_restored'})
                with self.assertRaises(ValueError):scene.analyze(run,False)

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_unintended_outbox_operation_fails_and_missing_payload_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            _,run=prepare_test_folder(Path(tmp));fabricated_capture(run)
            with (run/'save_events.csv').open(newline='') as stream:
                reader=csv.DictReader(stream);fields=reader.fieldnames;saves=list(reader)
            target=next(r for r in saves if r['phase']=='pose' and r['condition']=='final_return')
            target.update(new_operations='1',pending_operations='1',changed_feature_semantics='1',next_revision='2')
            with (run/'save_events.csv').open('w',newline='') as stream:
                writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader();writer.writerows(saves)
            rehash(run)
            with self.assertRaises(ValueError):scene.analyze(run,False)
            (run/'outbox_events.jsonl').write_text(json.dumps({'phase':'pose','condition':'final_return','scope':'normal','cycle':0,
                'edit':{'featureId':'e2_point_00','kind':'update','revision':1}})+'\n')
            rehash(run);result=scene.analyze(run,False)
            self.assertFalse(result['normal_pass']);self.assertEqual(result['new_outbox_operations_after_warmup']['normal'],1)

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_pack_preserves_evidence_and_refuses_archive_overwrite(self):
        import zipfile
        with tempfile.TemporaryDirectory() as tmp:
            _,run=prepare_test_folder(Path(tmp));fabricated_capture(run)
            before=numerical.digest(run/'scene_raw.csv');archive=scene.pack(run)
            with zipfile.ZipFile(archive) as z:
                self.assertIsNone(z.testzip())
                import hashlib
                self.assertEqual(hashlib.sha256(z.read(run.name+'/scene_raw.csv')).hexdigest(),before)
            self.assertEqual(numerical.digest(run/'scene_raw.csv'),before)
            with self.assertRaises(FileExistsError):scene.pack(run)

if __name__=='__main__':unittest.main()

