# E1: numerical round-trip collector

This measures the existing production `GeoUtils.cs`, with its float calibration
parameters and float reconstruction vectors. The application code is unchanged.
Python prepares inputs and uses PROJ for independent WGS84/ECEF reference/error
calculations; Python does not replace the C# system under test.

## Inputs and sampling

Supply an alignment JSON (`origin_wgs84`, `colmap_to_enu`) and the original OBJ
in the corresponding reconstruction frame. The optional splat `.asset` is metadata
only; its referenced binary splat data is not required for E1 and is not inspected.
Rotation, positive scale, origin, finite inputs and float range are validated.
No calibration repair or geographic fitting occurs.

Default sampling, fixed before inspecting pilot results:

- Every OBJ position record, including peripheral geometry.
- A 5 x 5 x 5 grid spanning the mesh's raw bounding box, including corners/edges.
- With an asset: a separate 5 x 5 x 5 grid spanning its declared bounds. Its
  reconstruction-frame interpretation is an explicit, unverified stress assumption.
- 2000 random points in the mesh bounding box per seed, seeds 101,202,303,404,505.

Bounding-box samples include empty space. Groups overlap and are reported
separately; they are not independent survey checkpoints. Mesh ENU dimensions are
computed from every vertex with the supplied double coefficients, not merely by
multiplying rotated raw bounding-box dimensions by a scale.

Geographic input samples are generated independently using PROJ from the requested
reconstruction points and the original double-precision JSON coefficients. In the
reconstruction-first direction, the starting point is explicitly float-rounded,
matching the production Vector3 entry point. That round trip does not score the
initial double-to-float input quantization as a second conversion error.

## Running in Unity (recommended native confirmation)

Install the measurement dependencies once:

```bat
python -m pip install -r LocalScripts/requirements-experiments.txt
```

Prepare a NEW output folder. Replace the input paths with your actual files:

```bat
python LocalScripts/run_numerical_roundtrip.py --alignment "D:\data\harokopio.json" --mesh "D:\data\harokopio.obj" --asset "D:\data\harokopio.asset" --output "D:\data\harokopio-numerical-native" --prepare-only
```

In Unity, select **Tools > Experiments > Numerical Round Trip > Run Prepared
Folder**, and choose that output folder. No scene or headset is required.
The collector writes `raw.csv` and `raw.csv.runtime.json` using the native Unity
Vector3 implementation and the current compiled production GeoUtils.
It refuses to overwrite previous measurements; use a fresh prepared folder for
a repeat. Then analyze:

```bat
python LocalScripts/run_numerical_roundtrip.py --analyze-run "D:\data\harokopio-numerical-native"
```

The downloaded pilot bundle also includes a `NativeUnityInputs` folder prepared
for this menu, without raw measurements. Copy it to a writable location first.
A changed production source requires freshly prepared inputs. Source identity is
checked with LF-normalized UTF-8 hashes (one trailing newline) to tolerate Windows
Git line endings and harmless trailing blank lines;
raw hashes are also retained.

## Running headlessly with Mono

Omit `--prepare-only`. The Python wrapper compiles the actual GeoUtils, data models,
outbox model dependency and shared collector. It supplies only a float Vector3
field carrier and an unused GameObject placeholder for a model dependency.
No engine transformations, physics or rendering are emulated.

Use the same environment variables as the existing C# checks: `AR2_MONO`,
`AR2_CSC`, `AR2_NEWTONSOFT_DLL`, optionally `AR2_MONO_CONFIG`, `MONO_PATH`, and
`LD_LIBRARY_PATH`. Explicit `--mono`, `--compiler`, `--newtonsoft` flags are also
available. The script tries mcs/mono on PATH and a Unity PackageCache Newtonsoft
DLL when variables are absent. Compiler failure leaves prepared inputs intact
for the native Unity route.

A headless result must be labelled headless; repeat in Unity before presenting
it as native engine evidence. Exact values can depend on runtime/JIT.

## Quantities measured

| Direction | Residual definition |
| --- | --- |
| `wgs_to_c_to_wgs` | Input WGS84 -> production reconstruction float -> production WGS84. ECEF difference projected onto reference ENU axes. |
| `c_to_wgs_to_c` | Input reconstruction float -> production WGS84 -> production reconstruction float. Residual mapped into ENU metres using float-rounded calibration coefficients, evaluated in double precision. |
| `c_to_wgs_vs_proj` | Production reconstructed-point-to-WGS84 result versus independently computed PROJ/ECEF position for that point and the float-rounded calibration. |
| `wgs_to_c_vs_proj` | Production inverse result mapped with the float-rounded calibration versus independently computed input ENU position. |

The independent checks reduce the risk of a mutually consistent forward/inverse
pair hiding conversion error. They do not validate the fitted calibration against
the real world. PROJ axis order is explicitly longitude,latitude,height
(`always_xy=True`, EPSG:4979 <-> EPSG:4978), with known equatorial-axis anchors.
All heights are interpreted as ellipsoidal; there is no geoid conversion.

For each direction and sample group, report signed ENU bias, horizontal/vertical/
3D median, empirical p95, maximum, secondary RMSE, exact zeros, nonfinite count
and threshold exceedances. Percentiles use linear interpolation at (n-1)*q.
The plot uses log error on x and the cumulative fraction on y. Exact zeros are
omitted from the log axis and counted explicitly; no artificial floor modifies
measurements. Nonfinite results remain failures in the denominator.

Default `--threshold-m 0.01` is a provisional numerical pilot budget, not a
mission or survey accuracy specification. It applies to 3D residuals; every
finite sample must stay within budget and no nonfinite result is allowed.
Lock final application tolerances independently before final experiments.

## Outputs and limits

- `prepared.json`: input/source hashes, calibration diagnostics, sampling,
  environment, threshold and provenance. A local synthetic workspace Git SHA is
  not a public release identifier; source hashes identify the exact measured code.
- `alignment.json`, `samples.csv`: unchanged calibration bytes and prepared inputs.
- `raw.csv`, `raw.csv.runtime.json`: actual C# outputs, rounded calibration and runtime.
- `errors.csv`: individual metric residuals; `summary.csv`, `summary.json`: summaries.
- `error_ecdf.png`, `report.md`: overview figure and readable report.

Analysis verifies sample/config/source identity, the collected raw-output hash,
row order, schema and counts. Preparation and analysis environments are recorded.
The raw source hash can differ with platform line endings, but the normalized
production source hash must match. Keep raw files immutable after collection.

This experiment measures no map hierarchy, manipulation, surface query,
load/save cycle, network traffic, frame rate or human task. Fit residuals stored
in a calibration file are not independent accuracy results. A small numerical
error does not establish geographic accuracy, mesh/splat agreement or correct
real-world altitude. These require the separate protocol experiments.

## Checking the collector

With dependencies and Mono configured:

```bat
python -m unittest discover -s LocalScripts/Tests -p "test_*.py" -v
```

The measurement tests exercise reference axes, input validation, real C# collection,
metric conversion, nonfinite accounting, immutable run folders and tamper detection.
Native Editor menu compilation can be checked against test doubles, but actual
native execution still requires Unity locally.
