"""Meaningful checks for the measurement/reference path, not geographic validation."""
import argparse
import csv
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest

REPO=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('numerical',REPO/'LocalScripts/run_numerical_roundtrip.py')
runner=importlib.util.module_from_spec(spec);spec.loader.exec_module(runner)
CONFIG={'origin_wgs84':{'lon':23.7,'lat':37.9,'alt':100},'colmap_to_enu':{'scale':2.5,'R_rowmajor':[1,0,0,0,1,0,0,0,1],'t':[10,20,30]}}

class NumericalTests(unittest.TestCase):
    def test_calibration_rejects_nonproper_rotation_and_invalid_origin(self):
        for r in [[-1,0,0,0,1,0,0,0,1],[2,0,0,0,1,0,0,0,1]]:
            config=json.loads(json.dumps(CONFIG));config['colmap_to_enu']['R_rowmajor']=r
            with self.assertRaises(ValueError):runner.calibration(config)
        config=json.loads(json.dumps(CONFIG));config['origin_wgs84']['lat']=91
        with self.assertRaises(ValueError):runner.calibration(config)

    def test_source_identity_survives_windows_line_endings_and_trailing_empty_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            a,b=Path(folder)/'a.cs',Path(folder)/'b.cs'
            a.write_bytes(b'public class Example {}\n\n');b.write_bytes(b'\xef\xbb\xbfpublic class Example {}\r\n')
            self.assertEqual(runner.source_digest(a),runner.source_digest(b))
            self.assertNotEqual(runner.digest(a),runner.digest(b))
            b.write_bytes(b'public class Different {}\n')
            self.assertNotEqual(runner.source_digest(a),runner.source_digest(b))

    def test_percentiles_and_failed_rows_cannot_produce_a_pass(self):
        self.assertEqual(runner.percentile([0,10],.95),9.5)
        summary=runner.summarize([(0,0,0)],2,.01)
        self.assertFalse(summary['pass']);self.assertEqual(summary['failed'],1)
        self.assertFalse(runner.summarize([(0,0,.02)],1,.01)['pass'])

    def test_mesh_bounds_use_positions_and_count_polygon_triangles(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'mesh.obj';path.write_text('v -2 0 1\nv 3 0 1\nv 3 4 1\nv -2 4 1\nf 1 2 3 4\n')
            _,result=runner.read_mesh(path)
            self.assertEqual(result['colmap_bounds']['extent'],[5,4,0]);self.assertEqual(result['triangles_fan_count'],2)

    @unittest.skipUnless(importlib.util.find_spec('pyproj'),'Install requirements-experiments.txt')
    def test_proj_axis_order_and_enu_reference(self):
        reference=runner.Reference((23.7,37.9,100))
        point=(100.,-200.,35.)
        result=reference.to_enu(reference.from_enu(point))
        for a,b in zip(point,result):self.assertAlmostEqual(a,b,places=6)

    @unittest.skipUnless(importlib.util.find_spec('pyproj') and (os.environ.get('AR2_CSC') or shutil.which('mcs')), 'Mono compiler and pyproj required')
    def test_actual_collector_and_analysis_reject_tampering_and_count_nonfinite(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);alignment=folder/'config.json';mesh=folder/'mesh.obj';output=folder/'run'
            alignment.write_text(json.dumps(CONFIG));mesh.write_text('v 0 0 0\nv 100 0 0\nv 0 100 0\nf 1 2 3\n')
            args=argparse.Namespace(alignment=alignment,mesh=mesh,asset=None,output=output,seeds=[11],random_per_seed=2,threshold_m=.01,mono=None,compiler=None,newtonsoft=None)
            runner.prepare(args);runner.execute_headless(output,args);summary=runner.analyze(output,make_plot=False)
            self.assertEqual(summary['samples'],130)
            self.assertTrue(all(s['pass'] for s in summary['summary']))
            with self.assertRaises(FileExistsError):runner.prepare(args)
            with (output/'raw.csv').open(newline='') as stream:rows=list(csv.DictReader(stream))
            rows[0]['geo_back_h']='NaN'
            with (output/'raw.csv').open('w',newline='') as stream:
                writer=csv.DictWriter(stream,fieldnames=runner.RAW_HEADER);writer.writeheader();writer.writerows(rows)
            with self.assertRaises(ValueError):runner.analyze(output,make_plot=False)
            # Explicitly simulate collector-generated nonfinite output with matching provenance.
            metadata=json.loads((output/'raw.csv.runtime.json').read_text());metadata['raw_sha256']=runner.digest(output/'raw.csv');runner.dump(output/'raw.csv.runtime.json',metadata)
            summary=runner.analyze(output,make_plot=False)
            self.assertTrue(all(s['failed']==1 and not s['pass'] for s in summary['summary'] if s['group']=='ALL'))
            rows[0]['id']='wrong'
            with (output/'raw.csv').open('w',newline='') as stream:
                writer=csv.DictWriter(stream,fieldnames=runner.RAW_HEADER);writer.writeheader();writer.writerows(rows)
            with self.assertRaises(ValueError):runner.analyze(output,make_plot=False)
            with (output/'samples.csv').open('a') as stream:stream.write('tamper')
            with self.assertRaises(ValueError):runner.analyze(output,make_plot=False)

if __name__=='__main__':unittest.main()
