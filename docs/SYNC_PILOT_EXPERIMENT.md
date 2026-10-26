# E5 first pilot: active-sync unchanged save and one point edit

This is a guided native Unity correctness pilot using the normal spawner,
editing workflow, durable outbox, synchronization coordinator and local HTTP
server. It records real request bodies and final local/server data. It does not
simulate Unity, mutate its saved JSON, or acknowledge operations for it.

The prepared server database is a private copy of the completed E2 fixture.
The target point's class is deliberately changed to `fire` during preparation
so it is easy to recognize; the manifest records that fixture change. Its ID,
coordinates and other properties remain unchanged. The database also includes
an unsupported-class probe with mixed-case IDs, extra properties and
nontrivial numeric precision. The probe should remain in both data copies and
stay unrendered. An unsupported-class warning is expected. It is not a deletion.

## Run with the supplied PreparedSyncPilot folder

1. Stop Unity Play mode and stop any old local API server using port 8080.
   Pull `experiments` and let Unity compile.
2. Extract `PreparedSyncPilot.zip` to a new directory, for example
   `D:\AR2\PreparedSyncPilot`. This directory contains `sync_pilot.json`,
   `server_seed.json` and `alignment.json`. Each prepared directory is single-use.
3. In a terminal at the repository root, run:

   ```bat
   python LocalScripts/run_sync_pilot.py --run "D:\AR2\PreparedSyncPilot"
   ```

   Python 3.9+ and the standard library are sufficient. The runner starts the
   private local server and prints the exact absolute Unity snapshot path.
   Keep this terminal open. It refuses an occupied port and never kills an
   existing server. It does not use `LocalScripts/local_features.json`.
4. In Unity, **before entering Play mode**, note the current Inspector values
   so you can restore them afterwards. Set the selected JsonSpawner's **Save
   File Path** to the printed `unity_state.json` path. Enable **Use Localhost**
   on its GeoJsonApiManager. Keep the normal source GeoJSON and alignment paths.
   Keep GeoJsonSyncCoordinator enabled, with remote polling enabled. Use the
   same Harokopio scene/calibration as the completed E2 capture.
5. Start Play mode and keep it **running and unpaused**. Wait for the Python
   terminal to show **STEP 1**. Release markers and finish/cancel any drawing.
6. Select the JsonSpawner GameObject, normally `CoordinatesLogic`, and choose:

   **Tools > Experiments > Synchronization > Record Unchanged Save**

   The helper records the actual Unity settings/endpoints and invokes the
   existing `SaveToJson` method once. Do not edit features during this stage.
   The runner verifies that a new durable save actually occurred, then observes
   the unchanged state for at least two configured remote-polling intervals,
   including scan/debounce/grace time. At the default 30-second remote interval
   this takes about 64 seconds. At least two real Unity feature/status reads
   must occur during observation; MQTT-only traffic does not establish this.
7. When Python shows **STEP 2**, select the same JsonSpawner and choose:

   **Tools > Experiments > Synchronization > Begin Point Edit**

   This selects and pings the exact target in the Hierarchy without moving it.
   Then move **only the printed target ID** once using the application's normal
   marker interaction, and release it. The supplied target is `e2_point_00`,
   now spawned as `fire_e2_point_00` in the Hierarchy. Enable the Fire filter.
   To locate it before recording, select `CoordinatesLogic` and use **Tools >
   Experiments > Synchronization > Select Test Point**. Select `CoordinatesLogic`
   again before invoking either recording action. Do not move it through the Inspector or edit its JSON:
   the production commit requires the normal interaction start/release events.
8. Keep Play mode unpaused while Python verifies delivery and a second quiet
   confirmation window covering two configured polling intervals. A pass
   requires exactly one successful target PATCH, a matching server/local/payload
   feature, an empty outbox, correct observer publication, and unchanged unrelated
   features, including the unsupported-class probe.
9. After **PASSED**, stop Play mode and restore the original Inspector values.
   The runner stops its private server automatically. Keep failed captures too.
10. The runner automatically creates a ZIP after **PASSED**. Send that ZIP.
    To package a failed capture for diagnosis, use the **Capture directory
    printed by Python**, not the prepared root:

    ```bat
    python LocalScripts/run_sync_pilot.py --pack-run "D:\AR2\PreparedSyncPilot\Captures\Capture_..."
    ```

    Send the resulting ZIP. Existing archives are never overwritten.

The helper refuses paused Play mode, a missing/incorrect spawner, unsettled
local saves, pending operations, active marker manipulation, an open draft,
non-pilot save paths, inactive sync/API components, non-localhost endpoints,
wrong ports, and actions outside the runner's current phase. It writes no scene
or asset file and changes no networking configuration itself.

You may grab, rotate or scale the map to make the target reachable. For this
single-edit pilot, position the map before **Begin Point Edit**, release it and
let saving settle, then keep it still during the point edit and confirmation.
Map movement must not change geographic feature data or create outbox operations.
This convenience does not constitute a separate recorded map-manipulation trial.

## Prepare another isolated run

For a new run/folder, use a completed E2 capture from the same site/calibration:

```bat
python LocalScripts/run_sync_pilot.py --prepare-from-capture "D:\AR2\Capture_20261026T183906Z_49d86a10" --output "D:\AR2\PreparedSyncPilot_02"
```

Preparation validates its input hashes, retains all coordinates, changes only
the declared target class to `fire`, and adds the foreign-class probe. It records
current source hashes. Use a newly prepared archive/folder after pulling this
change; the earlier animal-target archive contains different source hashes.
The input folder is immutable after preparation; after code changes, prepare a
new folder. The runner will not silently reuse an old client snapshot/outbox.

## Evidence and interpretation

Each capture contains the private server seed/database, initial and final Unity
and server snapshots, all HTTP requests (including GET/status/MQTT traffic),
Unity action markers, configuration exports, logs and a hashed `report.json`.
The original working server database is not used. The runner writes no Unity
snapshot and never uploads data to a production server. Real feature mutations
come only from the app's normal editing and sync path.

Analyze a returned capture with:

```bat
python LocalScripts/run_sync_pilot.py --analyze-run "D:\AR2\PreparedSyncPilot\Captures\Capture_..."
```

The analyzer rechecks integrity, native action markers, settings, request phases,
zero attempted feature writes for an unchanged save, exactly one confirmed target
update, matching final payload/data and preserved unrelated features. Failed
HTTP feature writes still fail the no-op criterion. Startup writes are retained
and rejected rather than hidden by settling until a favorable baseline.

This is one normal-online pilot, not a repeated two-client fault study, a
production-backend validation, or a measured local-save-to-Unity-ack latency
experiment. Observation/quiet-window duration is not delivery latency. The next
stages remain create/delete, offline recovery, restart, ambiguous responses and
two-client conflicts with explicit expected terminal states.

The collector's unit/integration tests include an explicitly synthetic client
against the real local HTTP server. Those temporary test captures are not native
Unity results. A real Unity capture from this procedure is still required.
