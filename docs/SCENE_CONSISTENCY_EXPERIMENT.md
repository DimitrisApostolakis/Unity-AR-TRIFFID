# E2: scene coordinates and unedited save/load

This collector uses the loaded scene's actual map Transform and its complete
parent hierarchy. It invokes the existing `JsonSpawner` coordinate lookup,
synchronous save/atomic-write path, outbox change capture and `SpawnFromJson`
reload. It measures the checked-out runtime code without changing serialized scene references.
Run native Unity measurements locally; compiler/safety checks with engine doubles
are not scientific scene measurements.

## Repeat after the coordinate-preserving persistence fix

The original failed baseline is `Capture_20261026T032049Z_f1ec03c8.zip`.
Keep that archive unchanged. Its SHA-256 is
`c9236750cda942cb69753a60c332a18220f272a408e7f43845e873a6143ac626`.
It completed the 100-cycle collection with source restoration, but failed the
evaluation: 4,474 new outbox operations in total, including 4,364 during measured
normal poses/cycles after warmup. Completion does not mean a passing experiment.

After pulling this fix, prepare a NEW input directory from the extracted baseline:

```bat
python LocalScripts/run_scene_consistency.py --repeat-from-capture "C:\path\Capture_20261026T032049Z_f1ec03c8" --output "C:\path\PreparedSceneInputs_CoordinateFix"
```

This verifies the old input/output hashes and copies the exact fixture,
alignment, poses and expected coordinates. It retains the original 0.01 m
numerical budget and records the comparison baseline. Only code provenance and
the explicit new validation requirements change. Reusing the old source manifest
directly will correctly fail the collector's source checks.

Run **Run Pilot (100 Cycles)** with the NEW directory, following the steps below.
The same 44 poses and four 100-cycle save/load conditions run first. A separate
disconnected fixture then checks deliberate point, line-vertex and polygon-first-
vertex edits through the production commit/save/reload path. The polygon check
includes its duplicate closure. Each edited feature must create one operation,
unrelated features/vertices must stay unchanged, and a repeated save/reload must
create no further operations. Evidence is written to `deliberate_edit_checks.json`.
These deterministic edits do not replace a manual controller/hand replay.

The repeat additionally requires exact equality of all stored geographic triples,
zero new outbox operations and zero feature-semantic changes in every unedited
save, including warmup and stress saves. Scene/world measurements still use the
original numerical budget. Do not run the 1,000-cycle extension until the new
100-cycle capture passes these checks and deliberate edits persist successfully.

The automated headless checks exercise the production persistence/edit methods,
including 100 saves of the supplied geographic fixture. They are regression
evidence, not a rerun of the native Unity map experiment. No new native capture
has been collected as part of this code change.

## Run the prepared Harokopio pilot

1. Stop Play mode and pull the `experiments` branch. Let Unity finish compiling.
2. Extract the supplied scene experiment ZIP. Find `PreparedSceneInputs`, which
   contains `prepared_scene.json`, `plan.json`, `alignment.json`, `fixture.geojson`
   and `expected.csv`.
3. Start the normal scene. Let alignment, map loading and autosave finish. Finish
   or cancel any draft drawing and release all markers.
4. Select the GameObject with the loaded `JsonSpawner` component, then **pause
   Play mode** using the Pause button. Keep Play mode running but paused.
5. Select **Tools > Experiments > Scene Consistency > Run Pilot (100 Cycles)**.
   Choose the **PreparedSceneInputs** folder, not the ZIP's outer folder.
6. The collector runs fixed poses and four persistence conditions, then opens a
   new `Capture_<timestamp>_<id>` directory. Play mode stays paused. The original
   map pose, transform change flags and informational log filter are restored.
7. Package the directory using your own returned capture path:

   ```bat
   python LocalScripts/run_scene_consistency.py --pack-run "C:\path\PreparedSceneInputs\Capture_..."
   ```

   This uses Python's standard library and creates a ZIP beside that capture
   folder. Send the ZIP for analysis. Existing capture archives are not overwritten.

To run the full proposed cycle count after the pilot, use **Run Extended (1000
Cycles)** with the same prepared inputs. It creates a separate capture directory.
The progress window supports cancellation. Retain incomplete captures as failure
evidence; the analyzer rejects them as completed experiments.

## Prepare another site or fresh source version

Use the E1 prepared inputs for the same calibration/OBJ. E1 measurements are not
needed for preparation; the unchanged `alignment.json`, `samples.csv` and
`prepared.json` are sufficient.

```bat
python LocalScripts/run_scene_consistency.py --prepare-from "D:\data\E1Inputs" --output "D:\data\PreparedSceneInputs"
```

The output directory must be new. The E1 input hashes are checked. Preparation
chooses 30 distinct OBJ positions with deterministic farthest-point sampling,
starting at a lexicographic boundary point. Four points on their geographic
convex hull form a valid counterclockwise polygon. The other positions form
20 POIs and one six-vertex line. The polygon has four distinct vertices plus a
duplicate closing coordinate: **30 distinct locations, 31 coordinate entries,
22 features**. The closure is retained and identified, not counted as another
independent location.

These are synthetic features at real mesh coordinates, not surveyed checkpoints
or edits from study participants. Sampling includes peripheral geometry and does
not validate the mesh-versus-splat relationship.

## Isolation and source protection

The loaded `JsonSpawner` supplies the actual map reference, current calibration
and scene provenance. Its features, prefabs, server connection and save path are
not used as mutable test data. A separate inactive fixture is parented beneath
an identity child of the actual map. Minimal POI/vertex prefabs and a second
`JsonSpawner` are owned by the collector, with all writes confined to the new
capture directory. It has no API manager or synchronization coordinator.

The map is temporarily transformed while Play mode is paused and the Editor
menu callback is running. The collector restores the captured local pose in a
`finally` block, also on cancellation/error. It checks the original feature
serialization and save-file hash before/after. The current running scene is not
saved. A pending save, active marker manipulation, open draft, unloaded calibration,
different calibration, altered prepared inputs or changed measured source causes
the run to be rejected.

The fixture is inactive so billboard motion, hand/controller input and its own
Update callbacks do not run. Surface height queries use a configured object
without a collider, preventing fallback to unrelated scene colliders. This
experiment measures coordinates and persistence; E3 measures height separately.
Generated nodes and line materials are immediately released between reloads to
bound memory while native Play-mode destruction would otherwise be deferred.

Informational setup logs are filtered during the batch; warnings/errors remain
visible and the previous log filter is restored afterward. The selected app's
existing data is never artificially acknowledged. Only the disconnected fixture
gets an explicit synthetic acknowledged baseline.

## Transform conditions

`plan.json` is prepared before measurement and records 44 fixed poses:

- Captured pose, translations, local rotation deltas and exact returns.
- Three sizes: 0.001, 0.01 and 1 world unit per ground metre along ENU Up.
- Twenty repeated combined translate/rotate/scale poses and a return.
- Stress positions `(o,o,-o)` for each per-axis offset `o=100,1000,10000` world
  units, at each size, with rotation; followed by an exact return.

The complete native local-to-world matrix and East/North/Up world-units-per-metre
lengths are recorded for each condition. The scale factor is computed after
rotation and preserves the original axis signs/reflection. `localScale=1` is not
assumed to be full metric scale. With a nonuniform parent hierarchy, normalizing
Up to 1 does not make all axes metric: report the three lengths and that limitation.

Normal/stress labels and the default **0.01 m** numerical budget are provisional
pilot definitions, not an established operating envelope or mission tolerance.
Stress failures remain in the data and receive separate summaries.

## Persistence conditions and initial-load behavior

Four conditions: captured pose, rotated tabletop (0.001 world unit/metre Up),
rotated full-metric-Up (1 world unit/metre Up), and the rotated tabletop at the
10000-unit per-axis stress offset. Each starts from the same prepared fixture.

The collector records initial load and exactly **one warmup save/reload** first.
The warmup's coordinate/metadata changes and outbox operations are retained.
Then it assigns a synthetic acknowledged baseline to the disconnected fixture.
It does not repeat settling until a favorable result, or acknowledge edits during
the measured cycles. Every measured cycle forces an otherwise unedited save,
reads the atomic file and reloads it through the production spawner. Raw scene
and stored coordinates are recorded at cycles 0 through 100 (pilot), or 1000
(extended). Full GeoJSON checkpoints remain at 0, 1, 10, 100 and, when run, 1000.

Forced saving tests whether an unedited save/reload changes semantics. It does
not assert that the timer naturally initiates all these writes. The synchronous
pause/quit flush shares the outbox, serialization and atomic-write stages with
normal saves. Neither save path converts display positions or normalizes feature
properties. Normal async save scheduling/latency is not measured by this collector.

## Analysis and pass criteria

```bat
python -m pip install -r LocalScripts/requirements-experiments.txt
python LocalScripts/run_scene_consistency.py --analyze-run "C:\path\Capture_..."
```

For every feature/vertex and condition, independent double-precision PROJ/ECEF
differences in ENU metres compare:

- Recomputed scene coordinates versus the original geographic input.
- Recomputed scene coordinates versus that trial's initial scene baseline.
- Coordinates actually stored on disk versus the original input.
- World-position-derived coordinates versus the original input and the initial
  world-position baseline. This uses the native inverse Transform and independent
  double-precision metric mapping, so an unchanged cached anchor cannot hide
  display-position error.
- Signed scene-derived increments between consecutive load/save cycles.
- Explicit inverse-world-position lookup versus the production cached lookup
  as a separate diagnostic. Both world-derived positions and the production
  coordinate path are scored; neither replaces the other.

Report max, p95, median, ENU bias, horizontal/vertical/3D errors, failures and
threshold exceedances per condition/geometry/scope. The pilot's normal-condition
criterion requires every main coordinate measurement within 10 mm, no nonfinite
values and zero new unintended outbox operations after warmup. The inverse-only
diagnostic and stress conditions are reported separately; their failures are not
hidden in an average. Inputs, raw files, actual measured source hashes, vertex
identity, all planned poses/cycles and operation payload/count coverage are checked.

`scene_raw.csv` contains native measurements; `scene_runtime.json` contains run,
hierarchy, calibration, source identity, completion/restoration flags and output
hashes. `save_events.csv` records each actual fixture save and its new/pending
outbox counts. `outbox_events.jsonl` retains newly created operation payloads,
including updates generated by unwanted coordinate/metadata changes.
`pose_matrices.json` records the tested transforms.

Analysis writes `scene_errors.csv`, `scene_summary.json`, `scene_summary.csv`
and an error ECDF. Exact zeros are omitted from a logarithmic axis; preserve
their counts in the summaries. Cancellation, a missing sample/cycle, altered
raw data or failure to restore the source cannot yield a completed pass.

**Actual network uploads are not measured**, rather than reported as zero.
A separate settled no-op trial with sync active and request instrumentation is
still required. Also remaining: a small human controller/hand manipulation replay,
native surface-height measurements, held-out geographic accuracy and performance.

