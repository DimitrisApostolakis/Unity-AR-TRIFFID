# In-app technical captures (participant studies deferred)

The numerical conversion and 100/1,000-cycle persistence pilots are already
verified. The normal-online no-op/point-edit capture is also verified. The next
three runs collect new native evidence; tests of the collectors are not results.
No runtime application behavior is changed by these Editor collectors.

## A. Automated height, polygon and deterministic core batch

1. Stop Play, pull `experiments`, and let Unity compile.
2. Open the normal Harokopio scene. Keep its original GeoJSON, alignment and save
   paths. Start Play, finish/cancel drawing and release all markers. Let saving
   settle, then **pause** Play. Select `CoordinatesLogic` / `JsonSpawner`.
3. Choose **Tools > Experiments > App Numerics > Run Height Geometry and Core
   Checks**. Choose an output parent folder, for example `D:\AR2\Results`.
   No prepared input ZIP or Save File Path change is required for this run.
4. The collector creates a unique `AppNumerics_...` folder and prints its path.
   It uses owned synthetic objects and meshes, compares the original feature
   data, save-file hash and map matrix afterwards, and retains incomplete runs.
5. At the repository root, analyze and package that exact capture folder:

   ```bat
   python LocalScripts/run_app_numerics.py --analyze-run "D:\AR2\Results\AppNumerics_..."
   python LocalScripts/run_app_numerics.py --pack-run "D:\AR2\Results\AppNumerics_..."
   ```

   Send the ZIP, including failures. `PASSED_NORMAL` does not erase diagnostic
   or stress failures. An incomplete/cancelled capture can still be packaged.

Coverage is fixed before collection:

- 2,160 surface observations: identity and actual site calibration; six display
  configurations; flat/sloped/two-level/bounded-miss/inactive/missing-collider
  surfaces; enabled/disabled colliders; five positions; signed offsets -2/0/2 m.
- Normal transforms include translation, rotation, uniform scaling, reflection
  and an explicitly nonuniform parent/child diagnostic configuration. The far
  origin at 10,000 world units with 0.001 local scale is a separate stress scope.
- A 5 mm **synthetic pilot** height budget is fixed in code and independently
  checked by the analyzer. The coordinate budget and exact storage-equality
  criteria of E1/E2 are unchanged. This is not a mission-accuracy threshold.
- Expected height is analytic ENU up from the known synthetic surface. The
  production height path returns geodetic-altitude difference; small curvature
  and float effects therefore remain in the measured error.
- The lower plane is the declared intended floor in the two-level fixture.
  A roof hit remains an intended-floor failure, even if its returned height is
  numerically stable. Those cases are reported separately, never relabeled as
  correct floor selection.
- Eleven polygon fixtures record input/output rings, expected acceptance,
  winding, closure, raw height/source/status arrays and rejection immutability.
  These execute the production validator; they do not claim interactive draft-UI
  coverage or arbitrary hole containment validation.
- Existing production state/serialization/atomic-file regression cases also run
  natively, with their individual console outcomes retained. Fake-server cases
  remain deterministic state tests, not actual HTTP fault experiments or crashes.

## B. Real Editor frame and save scaling

Run only after A compiles and captures successfully. This is a descriptive
**Unity Editor** workload capture. A standalone headset build/device capture is
still needed for target-device performance claims.

1. Restore/open the normal scene and let all pending edits/save work settle.
   Note the original coordinator enabled state. Disable the selected spawner's
   `GeoJsonSyncCoordinator` for this isolated benchmark.
2. Keep Play **running and unpaused**. Aim the Game camera at an existing visible
   POI near the centre of the view. Keep the Game view visible and the camera
   still. Do not grab/edit anything during the capture.
3. Select `CoordinatesLogic` and choose **Tools > Experiments > App Numerics >
   Run Editor Frame and Save Scaling**. Choose an output parent folder.
4. The approximately two-minute capture runs seven phases: original-scene idle
   baseline, then 10/100/1,000 additional features, each with idle and forced-save
   conditions. Every phase has 3 seconds of warm-up and 15 seconds of measurement.
   The mix is 80% points, 10% three-vertex lines and 10% four-corner polygons
   (five coordinate tuples including closure), all spawned through JsonSpawner.
5. Additional features and private save files belong to an isolated fixture.
   The original reconstruction and original features remain visible as baseline
   workload. No coordinator/API is attached to the fixture. Capture source-state
   comparisons must pass. Restore the original coordinator state afterwards.
6. Analyze/package the printed `AppPerformance_...` folder using the same
   `run_app_numerics.py` commands. Send that ZIP too.

The output includes per-frame Play-loop interval, allocated/managed memory,
camera position/orientation, actual active renderer count, save-request events
and main-thread SaveToJson call duration. Final saved features must exactly match
their fixture inputs and create zero outbox work. Summary frame distributions
use the nearest-rank p95/p99 convention, with warm-up/settling frames excluded.
Main-thread save-call time is **not** background disk completion or upload latency.
Editor/collector overhead is included. No operational frame-budget pass is assigned.
The mixture and original-scene baseline must be stated when comparing workloads.

Cancellation, script reload, paused/stopped Play or missing objects produce an
incomplete capture. **Cancel Frame Capture** stops the observer and cleans its
owned fixture. Keep failure artifacts. Frame capture never changes the map pose.

## C. Real offline-edit/recovery pilot

Use a NEW extraction of `PreparedSyncPilot_Fire_PatchFix.zip`, or prepare a new
folder from the completed E2 capture using `run_sync_pilot.py --prepare-from-capture`.
Do not reuse the previously measured folder/outbox. Current runtime source hashes
must match preparation. The new Editor numerical collectors do not invalidate
the previously prepared runtime sources.

```bat
python LocalScripts/run_sync_recovery.py --run "D:\AR2\Recovery01"
```

The script starts a private server and prints the exact `unity_state.json` FILE
path. Before Play, set the spawner Save File Path to that file, enable API Use
Localhost and the coordinator/remote polling, and retain normal GeoJSON/alignment.

Keep Play running/unpaused. Follow **Record Unchanged Save**. When STEP 2 appears,
choose **Begin Point Edit**, then **WAIT for `OFFLINE READY` before dragging**.
The helper selects `fire_e2_point_00`; enable its collider/Fire filter as needed.
Move it once and release. Connection errors are expected while the owned local
server is stopped. The observer reads and journals a real durable pending update
for at least five seconds, confirms no server change, restarts its server, and
checks exact delivery/local/server/payload agreement and empty outbox. It never
writes Unity's snapshot or acknowledges work for the app.

PASS automatically packages the capture. Restore original Inspector/API settings
after stopping Play. Failed runs retain their capture and can be packaged with
`run_sync_recovery.py --pack-run CAPTURE_FOLDER`. Verify a returned capture with
`run_sync_recovery.py --analyze-run CAPTURE_FOLDER`.

Observed recovery time starts at an independent successful API health check and
ends at the first observed stable local/server/outbox agreement. The polling
resolution and quiet confirmation window are reported separately. This is not
instrumented Unity callback latency. It is one outage/recovery pilot; repeated
fault schedules, application/process restarts, lost responses, two-client races
and production-backend semantics still require their own captures and oracles.

## Completion boundaries

These menus/scripts make the next measurements runnable; they are not completed
native results until the returned captures are verified. In-app fixtures can
establish numerical consistency and known-surface behavior. They cannot establish
physical geographic accuracy of the scene or robotic POIs without independent
reference coordinates, nor metric mesh/splat accuracy without a suitable reference.
Manual controller replay and polygon draft/UI rejection remain functional checks.
Participant learning/workload/usability studies are deferred as requested.
