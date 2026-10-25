#!/usr/bin/env python3
"""Prepare/run/analyze the production C# numerical conversion experiment (Python 3.9+).

Python supplies samples and independent PROJ reference/error calculations.
The system under test is the unchanged GeoUtils.cs, executed by Unity or Mono.
"""
from __future__ import annotations
import argparse
import csv
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

REPO = Path(__file__).resolve().parents[1]
RUNTIME = REPO / 'projects/TRIFFID/Assets/TRIFFID/Runtime'
COLLECTOR = REPO / 'projects/TRIFFID/Assets/TRIFFID/Editor/Experiments/NumericalRoundTripCollector.cs'
SOURCES = [RUNTIME / 'Geospatial/Coordinates/GeoUtils.cs', RUNTIME / 'Features/Model/GeoJsonDataModels.cs',
           RUNTIME / 'Features/Persistence/GeoJsonSyncState.cs', COLLECTOR, REPO / 'tests/experiments/NumericalHeadless.cs']
SAMPLE_HEADER = ['id', 'group', 'cx', 'cy', 'cz', 'lon', 'lat', 'height']
RAW_HEADER = ['id', 'group', 'c_start_x', 'c_start_y', 'c_start_z', 'geo_c_x', 'geo_c_y', 'geo_c_z',
              'geo_back_lon', 'geo_back_lat', 'geo_back_h', 'c_geo_lon', 'c_geo_lat', 'c_geo_h', 'c_back_x', 'c_back_y', 'c_back_z']


def digest(path):
    with Path(path).open('rb') as stream:
        sha = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b''): sha.update(chunk)
    return sha.hexdigest()


def source_digest(path):
    text = Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    return hashlib.sha256((text.rstrip() + "\n").encode("utf-8")).hexdigest()


def dump(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def f32(value):
    return struct.unpack('<f', struct.pack('<f', value))[0]


def matvec(matrix, vector):
    return tuple(sum(matrix[i * 3 + j] * vector[j] for j in range(3)) for i in range(3))


def add(a, b): return tuple(x + y for x, y in zip(a, b))
def sub(a, b): return tuple(x - y for x, y in zip(a, b))


def calibration(config, rounded=False):
    o, c = config['origin_wgs84'], config['colmap_to_enu']
    origin = tuple(float(o[k]) for k in ('lon', 'lat', 'alt'))
    s = float(c['scale']); r = list(map(float, c['R_rowmajor'])); t = list(map(float, c['t']))
    if len(r) != 9 or len(t) != 3 or not all(math.isfinite(x) for x in (*origin, s, *r, *t)):
        raise ValueError('Malformed/nonfinite calibration')
    if not -180 <= origin[0] <= 180 or not -90 < origin[1] < 90 or s <= 0:
        raise ValueError('Unsupported calibration origin/scale')
    if rounded: s, r, t = f32(s), list(map(f32, r)), list(map(f32, t))
    orth = max(abs(sum(r[k * 3 + i] * r[k * 3 + j] for k in range(3)) - (i == j)) for i in range(3) for j in range(3))
    det = r[0]*(r[4]*r[8]-r[5]*r[7])-r[1]*(r[3]*r[8]-r[5]*r[6])+r[2]*(r[3]*r[7]-r[4]*r[6])
    if orth > 1e-5 or abs(det - 1) > 1e-5: raise ValueError('Calibration rotation is not proper/orthonormal')
    return origin, s, r, t, {'determinant': det, 'max_orthogonality_residual': orth}


def enu_position(point, params):
    _, s, r, t, _ = params
    return add(tuple(s*x for x in matvec(r, point)), t)


class Reference:
    def __init__(self, origin):
        import pyproj
        self.version, self.proj_version = pyproj.__version__, pyproj.proj_version_str
        self.forward = pyproj.Transformer.from_crs(4979, 4978, always_xy=True)
        self.inverse = pyproj.Transformer.from_crs(4978, 4979, always_xy=True)
        self.origin = self.forward.transform(*origin, errcheck=True)
        lon, lat = map(math.radians, origin[:2])
        sl, cl, sp, cp = math.sin(lon), math.cos(lon), math.sin(lat), math.cos(lat)
        self.ecef_to_enu = (-sl, cl, 0., -sp*cl, -sp*sl, cp, cp*cl, cp*sl, sp)
        self.enu_to_ecef = tuple(self.ecef_to_enu[j*3+i] for i in range(3) for j in range(3))
        # Known WGS84 equatorial axes anchor the reference and axis order.
        for point, expected in [((0., 0., 0.), (6378137., 0., 0.)), ((90., 0., 0.), (0., 6378137., 0.))]:
            actual = self.forward.transform(*point, errcheck=True)
            if math.dist(actual, expected) > 1e-7: raise ValueError('Independent reference anchor failed')

    def from_enu(self, point):
        return self.inverse.transform(*add(self.origin, matvec(self.enu_to_ecef, point)), errcheck=True)

    def to_enu(self, point):
        return matvec(self.ecef_to_enu, sub(self.forward.transform(*point, errcheck=True), self.origin))

    def geographic_delta(self, returned, original):
        return matvec(self.ecef_to_enu, sub(self.forward.transform(*returned, errcheck=True),
                                           self.forward.transform(*original, errcheck=True)))


def bounds(points):
    low = [min(p[i] for p in points) for i in range(3)]
    high = [max(p[i] for p in points) for i in range(3)]
    return {'min': low, 'max': high, 'extent': list(sub(high, low))}


def read_mesh(path):
    vertices, faces, triangles = [], 0, 0
    with Path(path).open(encoding='utf-8-sig') as stream:
        for line_no, line in enumerate(stream, 1):
            fields = line.split()
            if not fields: continue
            if fields[0] == 'v':
                if len(fields) < 4: raise ValueError(f'Invalid vertex at OBJ line {line_no}')
                v = tuple(map(float, fields[1:4]))
                if not all(math.isfinite(x) and math.isfinite(f32(x)) for x in v): raise ValueError('Invalid vertex')
                vertices.append(v)
            elif fields[0] == 'f': faces += 1; triangles += max(0, len(fields)-3)
    if not vertices: raise ValueError('No mesh vertices found')
    return vertices, {'vertices': len(vertices), 'faces': faces, 'triangles_fan_count': triangles, 'colmap_bounds': bounds(vertices)}


def read_asset(path):
    text = Path(path).read_text(encoding='utf-8-sig')
    count = re.search(r'^\s*m_SplatCount:\s*(\d+)', text, re.M)
    if not count: raise ValueError('Asset has no splat-count metadata')
    result = {'splat_count_from_metadata': int(count.group(1)), 'bounds_frame_not_independently_verified': True}
    for name, key in [('m_BoundsMin', 'min'), ('m_BoundsMax', 'max')]:
        match = re.search(name + r':\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}', text)
        if not match: raise ValueError('Asset bounds missing')
        result[key] = list(map(float, match.groups()))
    if not all(math.isfinite(x) for x in result['min'] + result['max']) or any(a > b for a,b in zip(result['min'],result['max'])):
        raise ValueError('Invalid asset bounds')
    return result


def grid(low, high):
    return itertools.product(*[[a + (b-a)*i/4 for i in range(5)] for a,b in zip(low,high)])


def source_hashes(): return {str(p.relative_to(REPO)): digest(p) for p in SOURCES}


def prepare(args):
    config = json.loads(args.alignment.read_text(encoding='utf-8-sig'))
    params, rounded = calibration(config), calibration(config, True)
    ref = Reference(params[0])
    vertices, mesh = read_mesh(args.mesh)
    mesh['enu_bounds_m'] = bounds([enu_position(p, params) for p in vertices])
    asset = read_asset(args.asset) if args.asset else None
    groups = [('mesh_vertices', vertices), ('mesh_bbox_grid', list(grid(mesh['colmap_bounds']['min'],mesh['colmap_bounds']['max'])))]
    if asset: groups.append(('asset_bbox_grid_unverified_frame', list(grid(asset['min'], asset['max']))))
    for seed in args.seeds:
        rng = random.Random(seed)
        groups.append((f'random_mesh_bbox_seed_{seed}', [tuple(rng.uniform(a,b) for a,b in
            zip(mesh['colmap_bounds']['min'],mesh['colmap_bounds']['max'])) for _ in range(args.random_per_seed)]))
    # All parsing/validation precedes output creation. Never overwrite a prior run.
    args.output.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(args.alignment, args.output/'alignment.json')
    with (args.output/'samples.csv').open('w', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream); writer.writerow(SAMPLE_HEADER)
        index = 0
        for group, points in groups:
            for p in points:
                geographic = ref.from_enu(enu_position(p, params))
                writer.writerow([str(index), group, *p, *geographic]); index += 1
    try: commit = subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True,stderr=subprocess.DEVNULL).strip()
    except (OSError,subprocess.CalledProcessError): commit = None
    manifest = {'schema_version': 1, 'stage': 'pilot_numerical_consistency', 'created_utc': datetime.now(timezone.utc).isoformat(),
        'git_commit_local': commit, 'sources_sha256': source_hashes(), 'geo_utils_lf_sha256': source_digest(SOURCES[0]), 'analysis_driver_sha256': digest(Path(__file__)),
        'reference_anchors': 'PROJ WGS84 equatorial axes: max residual < 1e-7 m', 'python': platform.python_version(),
        'pyproj': ref.version, 'proj': ref.proj_version, 'platform': platform.platform(),
        'threshold_m': args.threshold_m, 'threshold_status': 'provisional engineering target; not mission accuracy',
        'sample_count': index, 'groups': {name: len(points) for name,points in groups}, 'seeds': args.seeds,
        'random_per_seed': args.random_per_seed, 'mesh': mesh, 'asset': asset,
        'input_sha256': {str(p.resolve()): digest(p) for p in [args.alignment,args.mesh]+([args.asset] if args.asset else [])},
        'alignment_sha256': digest(args.output/'alignment.json'), 'samples_sha256': digest(args.output/'samples.csv'),
        'calibration_double_diagnostics': params[-1], 'calibration_float_diagnostics': rounded[-1],
        'fit_statistics_not_independent_accuracy': config.get('fit_stats'),
        'sampling_note': 'Mesh vertices are observed geometry. Bbox grids/random points include empty space. Asset bbox frame is an explicit unverified stress assumption.'}
    dump(args.output/'prepared.json', manifest)
    print(f'Prepared {index} samples in {args.output}',flush=True)
    return args.output


def execute_headless(directory, args):
    mono = args.mono or os.environ.get('AR2_MONO') or shutil.which('mono')
    compiler = args.compiler or os.environ.get('AR2_CSC') or shutil.which('mcs')
    dll = args.newtonsoft or os.environ.get('AR2_NEWTONSOFT_DLL')
    if not dll:
        candidates=list((REPO/'projects/TRIFFID/Library/PackageCache').glob('com.unity.nuget.newtonsoft-json*/Runtime/Newtonsoft.Json.dll'))
        candidates += [Path('/usr/lib/cli/Newtonsoft.Json-5.0/Newtonsoft.Json.dll')]
        dll=next((str(p) for p in candidates if p.is_file()),None)
    if not mono or not compiler or not dll or not Path(dll).is_file():
        raise RuntimeError('Prepared inputs are intact. Set AR2_MONO, AR2_CSC, AR2_NEWTONSOFT_DLL, or use Unity: Tools > Experiments > Numerical Round Trip > Run Prepared Folder; then --analyze-run FOLDER.')
    options=['--config',os.environ['AR2_MONO_CONFIG']] if os.environ.get('AR2_MONO_CONFIG') else []
    with tempfile.TemporaryDirectory(prefix='triffid-numerical-') as build:
        executable=Path(build)/'Numerical.exe'
        command=([mono,*options,compiler] if str(compiler).endswith('.exe') else [compiler])
        command+=['-langversion:latest','-warnaserror','-out:'+str(executable),'-r:'+str(dll),*map(str,SOURCES)]
        subprocess.run(command,check=True)
        shutil.copyfile(dll,Path(build)/'Newtonsoft.Json.dll')
        subprocess.run([mono,*options,str(executable),str(directory),str(SOURCES[0])],check=True)


def percentile(values, q):
    ordered=sorted(values); position=(len(ordered)-1)*q
    lo=math.floor(position); hi=math.ceil(position)
    return ordered[lo]+(ordered[hi]-ordered[lo])*(position-lo)


def summarize(values, attempted, threshold):
    if not values: return {'attempted':attempted,'finite':0,'failed':attempted,'pass':False}
    horizontal=[math.hypot(p[0],p[1]) for p in values]; vertical=[abs(p[2]) for p in values]; full=[math.sqrt(sum(v*v for v in p)) for p in values]
    result={'attempted':attempted,'finite':len(values),'failed':attempted-len(values),
            'bias_enu_m':[sum(p[i] for p in values)/len(values) for i in range(3)],
            'exact_zero_3d':sum(x==0 for x in full),'exceeding_3d_threshold':sum(x>threshold for x in full)}
    for name, data in [('horizontal',horizontal),('vertical',vertical),('3d',full)]:
        result[name]={'median_m':percentile(data,.5),'p95_m':percentile(data,.95),'max_m':max(data),
                      'rmse_m':math.sqrt(sum(x*x for x in data)/len(data))}
    result['pass']=result['failed']==0 and result['exceeding_3d_threshold']==0
    return result


def analyze(directory, make_plot=True):
    manifest=json.loads((directory/'prepared.json').read_text()); runtime=json.loads((directory/'raw.csv.runtime.json').read_text())
    if manifest['schema_version']!=1 or runtime['collector_version']!=1: raise ValueError('Unsupported run version')
    for key, filename in [('alignment_sha256','alignment.json'),('samples_sha256','samples.csv')]:
        if digest(directory/filename)!=manifest[key] or runtime[key]!=manifest[key]: raise ValueError('Input hash mismatch: '+filename)
    if digest(directory/'raw.csv') != runtime['raw_sha256']: raise ValueError('Raw output hash mismatch')
    if runtime.get('geo_utils_lf_sha256')!=manifest['geo_utils_lf_sha256']:
        raise ValueError('Measured GeoUtils source differs from the prepared version')
    params=calibration(json.loads((directory/'alignment.json').read_text(encoding='utf-8-sig')),True)
    ref=Reference(params[0]); series={}; attempts={}; sample_count=0
    error_header=['id','group','direction','status','dE_m','dN_m','dU_m','horizontal_m','vertical_m','error3d_m']
    with (directory/'samples.csv').open(newline='') as a, (directory/'raw.csv').open(newline='') as b, (directory/'errors.csv').open('w',newline='') as output:
        inputs,raw=csv.DictReader(a),csv.DictReader(b); writer=csv.writer(output);writer.writerow(error_header)
        if inputs.fieldnames!=SAMPLE_HEADER or raw.fieldnames!=RAW_HEADER: raise ValueError('CSV schema mismatch')
        for expected,observed in itertools.zip_longest(inputs,raw):
            if expected is None or observed is None or any(expected[k]!=observed[k] for k in ('id','group')):
                raise ValueError('Raw rows do not match prepared samples')
            v={k:float(x) for k,x in observed.items() if k not in ('id','group')}
            sample_count+=1; finite=all(math.isfinite(x) for x in v.values())
            measures={name:None for name in ['wgs_to_c_to_wgs','c_to_wgs_to_c','c_to_wgs_vs_proj','wgs_to_c_vs_proj']}
            if finite:
                start=tuple(v['c_start_'+axis] for axis in 'xyz')
                requested=tuple(float(expected[k]) for k in ('cx','cy','cz'))
                if start!=tuple(map(f32,requested)): raise ValueError('Collector input float conversion mismatch')
                g0=tuple(float(expected[k]) for k in ('lon','lat','height'))
                g1=tuple(v['geo_back_'+axis] for axis in ('lon','lat','h'))
                cg=tuple(v['c_geo_'+axis] for axis in ('lon','lat','h'))
                back=tuple(v['c_back_'+axis] for axis in 'xyz');gc=tuple(v['geo_c_'+axis] for axis in 'xyz')
                measures['wgs_to_c_to_wgs']=ref.geographic_delta(g1,g0)
                measures['c_to_wgs_to_c']=tuple(params[1]*x for x in matvec(params[2],sub(back,start)))
                measures['c_to_wgs_vs_proj']=sub(ref.to_enu(cg),enu_position(start,params))
                measures['wgs_to_c_vs_proj']=sub(enu_position(gc,params),ref.to_enu(g0))
            for direction,delta in measures.items():
                good=delta is not None and all(math.isfinite(x) for x in delta)
                for group in (observed['group'],'ALL'):
                    key=(direction,group);attempts[key]=attempts.get(key,0)+1
                    series.setdefault(key,[])
                    if good: series[key].append(delta)
                writer.writerow([observed['id'],observed['group'],direction,'ok' if good else 'nonfinite',
                    *delta,math.hypot(delta[0],delta[1]),abs(delta[2]),math.sqrt(sum(x*x for x in delta))] if good else
                    [observed['id'],observed['group'],direction,'nonfinite',*['']*6])
    if sample_count!=manifest['sample_count'] or sample_count!=runtime['sample_count']: raise ValueError('Sample count mismatch')
    summaries=[dict(direction=d,group=g,**summarize(values,attempts[(d,g)],manifest['threshold_m'])) for (d,g),values in series.items()]
    summary={'stage':'pilot_numerical_consistency','profile':runtime['profile'],'samples':sample_count,
        'threshold_m':manifest['threshold_m'],'threshold_status':manifest['threshold_status'],
        'quantile_method':'linear interpolation at (n-1)*q','runtime':runtime,
        'analysis_environment':{'python':platform.python_version(),'pyproj':ref.version,'proj':ref.proj_version,'analysis_driver_sha256':digest(Path(__file__))},
        'summary':summaries,'limitations':['No map hierarchy/manipulation, physics, save cycles, network or user input measured.',
         'No independent geographic accuracy established. Calibration fit residuals are not held-out accuracy.',
         'Asset bounds are metadata only; splat binaries and relative renderer/mesh alignment were not verified.',
         'Input sample groups overlap and are deterministic; counts are not independent survey checkpoints.']}
    dump(directory/'summary.json',summary)
    with (directory/'summary.csv').open('w',newline='') as stream:
        writer=csv.writer(stream);writer.writerow(['direction','group','attempted','failed','max_3d_m','p95_3d_m','rmse_3d_m','exceeding','pass'])
        for row in summaries:writer.writerow([row['direction'],row['group'],row['attempted'],row['failed'],row.get('3d',{}).get('max_m'),row.get('3d',{}).get('p95_m'),row.get('3d',{}).get('rmse_m'),row.get('exceeding_3d_threshold'),row['pass']])
    if make_plot: plot(directory,series,manifest['threshold_m'])
    lines=['# Harokopio numerical consistency pilot' if manifest['asset'] else '# Numerical consistency pilot','',
           f"Profile: {runtime['profile']}",f"Samples: {sample_count}",f"Provisional numerical budget: {manifest['threshold_m']} m",'',
           '| Direction | Max 3D (m) | p95 3D (m) | Failed | Threshold result |','| --- | ---: | ---: | ---: | --- |']
    for row in summaries:
        if row['group']=='ALL':
            lines.append(f"| {row['direction']} | {row.get('3d',{}).get('max_m',float('nan')):.9g} | {row.get('3d',{}).get('p95_m',float('nan')):.9g} | {row['failed']} | {'PASS' if row['pass'] else 'FAIL'} |")
    lines+=['','These results test numerical conversion only. Native Unity confirmation is needed for a headless run.','',*['- '+x for x in summary['limitations']]]
    (directory/'report.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    for row in summaries:
        if row['group']=='ALL': print(row['direction'],json.dumps(row),flush=True)
    return summary


def plot(directory,series,threshold):
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
    except ImportError:
        print('Plot skipped: install matplotlib.',file=sys.stderr);return
    fig,ax=plt.subplots(figsize=(8,5))
    for (direction,group),values in series.items():
        if group!='ALL':continue
        data=sorted(math.sqrt(sum(x*x for x in p)) for p in values)
        positive=[(x,(i+1)/len(data)) for i,x in enumerate(data) if x>0]
        if positive:ax.plot([p[0] for p in positive],[p[1] for p in positive],label=f'{direction} (zeros={len(data)-len(positive)})')
    ax.axvline(threshold,color='black',linestyle='--',label='Provisional numerical budget')
    ax.set_xscale('log');ax.set_xlabel('3D error (m); exact zeros omitted and counted in legend');ax.set_ylabel('Empirical cumulative fraction')
    ax.set_ylim(0,1.01);ax.grid(True,which='both',alpha=.2);ax.legend(fontsize=8);fig.tight_layout()
    fig.savefig(directory/'error_ecdf.png',dpi=180);plt.close(fig)


def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--alignment',type=Path);parser.add_argument('--mesh',type=Path);parser.add_argument('--asset',type=Path)
    parser.add_argument('--output',type=Path);parser.add_argument('--prepare-only',action='store_true');parser.add_argument('--analyze-run',type=Path)
    parser.add_argument('--seeds',type=int,nargs='+',default=[101,202,303,404,505]);parser.add_argument('--random-per-seed',type=int,default=2000)
    parser.add_argument('--threshold-m',type=float,default=.01);parser.add_argument('--mono');parser.add_argument('--compiler');parser.add_argument('--newtonsoft')
    args=parser.parse_args(argv)
    try:
        if args.analyze_run:
            result=analyze(args.analyze_run.resolve())
        else:
            if not args.alignment or not args.mesh:parser.error('--alignment and --mesh are required when preparing a run')
            if args.random_per_seed<0 or len(args.seeds)!=len(set(args.seeds)) or not math.isfinite(args.threshold_m) or args.threshold_m<=0:
                parser.error('Use unique seeds, a nonnegative sample count and a finite positive threshold')
            if args.output is None:args.output=REPO/'projects/TRIFFID/Logs/NumericalRoundTrip'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
            args.output=args.output.resolve();directory=prepare(args)
            if args.prepare_only:return 0
            execute_headless(directory,args);result=analyze(directory)
        return 0 if all(r['pass'] for r in result['summary'] if r['group']=='ALL') else 1
    except (OSError,ValueError,KeyError,OverflowError,csv.Error,RuntimeError,ImportError,subprocess.CalledProcessError) as error:
        print(f'FAILED: {error}',file=sys.stderr);return 2


if __name__=='__main__':raise SystemExit(main())
