# Finish the Harokopio technical evaluation

Scope locked by the user on 27 October 2026: Harokopio only; Unity Editor
performance only; no standalone performance campaign or extra site. SRT/COLMAP
registration evidence will arrive later. Participant studies remain deferred.
Completed native captures remain results; new scripts/tests are preparation.

## 1. Repeated real-HTTP synchronization matrix

Pull `experiments` with Play stopped. Unity must compile the new Editor-only
`SyncMatrixBridge`. A Unity/C# compiler is unavailable in the preparation
environment, so native compilation and execution remain to be verified on the
user's installation. Python oracle tests and real HTTP fault tests are separate. Thirty-eight
Python tests covering the matrix, existing sync pilots and numerical analyzers
passed during preparation; these are software checks, not new native results.
No production runtime code or existing prepared-input hashes were changed.

Extract a NEW `PreparedSyncPilot_Fire_PatchFix.zip` folder. Use the directory
containing `sync_pilot.json`, not a parent directory. Keep all prior captures.
From the repository root:

```bat
python LocalScripts/run_sync_matrix.py --run "D:\Experiments\SyncMatrix01" --repetitions 20
```

The runner prints the full private `unity_state.json` file path. BEFORE Play,
set that path in JsonSpawner; enable API Use Localhost, coordinator and remote
polling. Keep source GeoJSON/alignment. These Inspector settings must exist
before Play because the restart condition reloads the scene's saved settings.
Save the test scene configuration before entering Play; restore original paths
and API settings afterwards. Do not use a production backend for this capture.

Enter Play unpaused. Select CoordinatesLogic / JsonSpawner, then choose:
**Tools > Experiments > Synchronization > Attach Automated Matrix**.
No point dragging, menu actions or terminal Enter presses are needed afterwards.
The runner requests production API calls through the explicitly attached bridge.
Python only modifies the private server and command files, never Unity's saved
features/outbox or acknowledgements. Creation uses the production AddPoint API
with an owned marker GameObject; this does not measure rendered prefab or UI use.

Seven conditions, 20 repetitions each, 140 trials:

| Condition | Expected result |
| --- | --- |
| Online point creation + deletion | One PUT, one DELETE; exact payload, unique assigned server ID, unrelated data unchanged |
| Offline point creation + deletion | Two durable pending operations retained at least 5 s; exact convergence after server restart |
| Play-mode restart with pending update | Native exited/entered Play events, pending data restored before reconnect, one delivered PATCH |
| Lost creation response | Server commits a create, then closes the connection without its successful response; reconcile to one server record without another PUT |
| One-shot HTTP 503 | Request rejected before mutation; retry produces exactly one successful PATCH |
| External edit while local edit pending | External server version wins; no stale local feature upload |
| External deletion while local edit pending | Feature remains deleted; no resurrection/upload |

The complete batch takes tens of minutes because every trial confirms quiet
convergence across two configured poll intervals. Do not pause Unity, manipulate
features, move the map, change settings, edit scripts or select terminal text.
It intentionally stops/starts Editor Play during the restart condition. It does
NOT terminate Unity or simulate a mid-write crash. Distinct lifecycle events
verify the restart; disabling domain reload does not turn it into a process test.

The server assigns new IDs to creates. Original unsupported features must stay
exact. A changed supported target may gain its normal height metadata, but
unrelated features/properties must not change. Failed cases stop the batch and
retain evidence. No failures are excluded from a successful-rate denominator.
A failed batch must be investigated rather than repeatedly rerun until it passes.

After success: detach the bridge using **Detach Automated Matrix**, stop Play,
restore original Inspector/API settings, and send the printed ZIP. Analysis:

```bat
python LocalScripts/run_sync_matrix.py --analyze-run "D:\Experiments\SyncMatrix01\Captures\Matrix_..."
```

For a failed run, stop Play/detach before packing its retained folder:

```bat
python LocalScripts/run_sync_matrix.py --pack-run "D:\Experiments\SyncMatrix01\Captures\Matrix_..."
```

Raw evidence includes per-command native responses, exact before/after feature
collections, offline durable snapshots, Play lifecycle evidence, HTTP audit,
fault events, assigned IDs and final outbox/state. The analyzer rechecks final
oracles and trial coverage. Timing is a 0.25 s sampled convergence observation,
not instrumented save-to-callback latency. For offline cases its origin is API
health after reconnection; for online cases it is the observation start after
commands. Do not pool these as one latency metric.

Remaining unmeasured fault boundaries: process kill/mid-write crash, retry
exhaustion, deliberately forced read-update races, a second full application
instance and production-backend semantics. External conflicts here are issued
by the server harness as another writer, not a second rendered Unity client.
State these boundaries or add separate experiments if corresponding claims are
required. This finite matrix is not an assertion of universal fault tolerance.

## 2. Two more Editor performance captures

The existing verified run counts as run 1. Collect runs 2 and 3 using the same
**Run Editor Frame and Save Scaling** menu, original Harokopio scene content,
map pose, fixed Game camera, view size, graphics/VSync settings and workload.
Each run creates a fresh output folder. Coordinator disabled, Play unpaused,
no user input. Restore the coordinator afterwards. Analyze/package each run
using `run_app_numerics.py` and retain the first run's disclosed clock correction.

Report each run and between-run variation. Frames within a run are correlated;
they are not independent repeated trials. If views differ, keep the results
separate rather than averaging them as matching workloads. Save-call stalls
are measured findings; optimizing them is a separate task, not a prerequisite
for reporting the existing behavior. No headset performance claim is planned.

## 3. Short manual controller/drawing verification

Use a private COPY of a completed fixture snapshot and its .bak if present;
never overwrite the original captured evidence. Before Play set JsonSpawner's
Save File Path to that copy, enable localhost if needed, and disable its
synchronization coordinator so the local checks cannot reach another server.
Keep your normal alignment. Copy the saved file at each step, retaining the
complete feature collection and outbox; record the actions with a screen capture.
Do not interpret these developer checks as participant study results.

1. Finish/cancel drawings and release all markers. Save and retain baseline.
2. Through actual controller interactions translate, rotate and scale the map,
   release it, save, and retain a second file. All geographic feature data and
   pending operations must equal baseline. Repeat with small/large normal poses.
3. Move one point, release, save. Retain file. Only that point's coordinates and
   expected height metadata may change; repeat unchanged save must add no work.
4. Move one existing line vertex, release/save. Retain file. Other vertices and
   unrelated properties/features must remain exact.
5. Move a polygon closure vertex, release/save. Retain file. First/last tuples
   must remain equal; unrelated vertices/features stay exact.
6. Draw and save a valid line and polygon; retain file. Reload the private saved
   data and verify their geometry, IDs and metadata are preserved.
7. Attempt a crossing-edge polygon and a zero-area polygon through the drawing
   UI. Retain validation messages and before/after files; rejection must preserve
   existing features. Cancel an unfinished drawing and check no feature is added.

Send the staged snapshot files, action notes and screen capture so exact changes
can be verified. Stop Play and restore original source/save/API settings before
starting any participant session. A failed UI check is a bug to resolve first.

## Writing boundary

We can write the numerical consistency, persistence, synthetic height, Editor
performance and completed synchronization results now. Fill the remaining matrix
and manual-check results after captures are verified. Keep physical registration
accuracy/robotic POI accuracy pending until the SRT/COLMAP and independent
reference provenance are available; fitting residuals are not held-out accuracy.
