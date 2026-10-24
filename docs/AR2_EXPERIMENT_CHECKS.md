# AR2 correctness checks

The `experiments` branch contains geographic placement, surface-height,
polygon-validation, and persistent synchronization fixes. Run the Unity checks
and the application scenarios below before collecting paper measurements.

## Changes

- A failed surface search keeps the supplied longitude and latitude. There is
  no search from the mesh centre. Missing elevation has a magenta preview and
  an unavailable label, and its preview altitude is not exported.
- Surface rays use ENU Up through the complete map hierarchy, including
  reflected and nonuniform scale. Ray distances and surface offsets are in
  physical metres. Height offsets retain their sign.
- Points and annotation vertices store height provenance and query status.
  Values identify a mesh measurement, a retained value, an altitude-delta
  estimate, or an unavailable height.
- Polygon rings reject crossing or overlapping edges, repeated vertices,
  nonfinite coordinates, and zero area. Exterior rings are counterclockwise
  and holes clockwise. Reordering keeps vertex metadata aligned. A failed
  drawing save leaves the drawing editable.
- Features and their pending operations share one atomic local file. Pending
  deletions retain tombstones. Restart restores unfinished work, and delayed
  acknowledgements cannot clear a newer revision. Pending features are
  reconciled separately so unrelated remote features can still arrive.
- The API parser preserves missing altitude. Save sequencing prevents an older
  asynchronous write from replacing a newer synchronous flush.

## Automated checks run without Unity

Run from the repository root with Mono, its C# compiler, and Newtonsoft.Json:

```bash
bash scripts/test_ar2_core.sh
```

If these dependencies are not in their usual locations, set `AR2_MONO`,
`AR2_CSC`, and `AR2_NEWTONSOFT_DLL`. A private Mono installation can also use
`AR2_MONO_CONFIG`, `MONO_PATH`, and `LD_LIBRARY_PATH`.

The verified result is **52 core checks and 12 coordinator checks passed**.
The runner compiles the production data models, outbox, polygon validator,
atomic writer, payload parser, and synchronization coordinator with compiler
warnings treated as errors. Coordinator checks use simulated scheduling,
transport, and local-file ownership. They do not execute Unity physics,
the complete application, or a real server.

## Repeated JSON work and scene pauses

The coordinator checks file metadata at its local scan interval. It parses and
hashes JSON when the file or persistence version changes, a pending operation
becomes due, or a remote check is requested/due. An unchanged idle snapshot no
longer gets parsed and hashed on every scan.

In Play mode, inspect **Runtime JSON Diagnostics** on
`GeoJsonSyncCoordinator`. **Local Snapshot Read Count** counts its actual JSON
reads, and **Local Snapshot Write Count** counts its successful commits. These
counters cover the coordinator, not direct `JsonSpawner` saves, which produce
the `Saved to:` Console message. Occasional reads during remote polling and
writes for actual edits, dispatch records, and acknowledgements are expected.

Move, rotate, and scale only the map for 20 seconds, then leave it idle. Record
both counters before and after, the number of `Saved to:` messages, and whether
the pause recurs. This distinguishes repeated JSON work from other costs such
as rendering or collider updates. A profiler capture is needed to attribute
any remaining frame-time spike.

For a hitch coinciding with `Saved to:`, enable **Log Save Stage Timings** on
`JsonSpawner` (enabled by default). Its existing save message includes an
`[AR2 SaveTiming]` suffix. Durations are milliseconds:

- `world`, `normalize`, `outbox`, `labels`, and `serialize` measure synchronous
  preparation on the main thread.
- `backgroundWrite` measures the background file operation, including its file
  lock. A long background write alone does not establish a main-thread stall.
- `resumeWait` measures the delay between worker completion and the main-thread
  continuation. A long delay can mean the main thread was busy elsewhere.
- `followUp` indicates a queued save request or a newer persistence version.
- `[AR2 SyncTiming]` reports synchronous synchronization commits taking at
  least 16 milliseconds. This threshold controls diagnostic output only.

Capture the timing suffix and any nearby SyncTiming messages for the same
spike. In the CPU Profiler, the `AR2.Save.*` samples identify save preparation,
the completion log, and immediate queued preparation. `AR2.Sync.Commit`
identifies synchronous synchronization persistence. Keep Deep Profile off for
an initial capture. Stage timings do not measure garbage collection or other
work outside these samples, and the update itself does not establish the
cause of a scene hitch.

## Unity checks to run locally

1. Open `projects/TRIFFID` in Unity **6000.3.7f1** on `experiments`.
2. Wait for scripts to compile. Resolve any compiler errors before proceeding.
3. Outside Play mode, select **AR2 > Experiments > Run Regression Checks**.
4. Inspect the Console. Expected results are 52 passing core checks and
   **22 passing Unity checks**, with no failing checks.

The Unity checks create and remove temporary objects. They check signed
offsets of -2, 0, and +2 metres, out-of-mesh searches, map translation and
rotation, display scales of 0.01 and 3, mirrored scale, and a nonuniformly
scaled hierarchy. They also check rejection of invalid calibration scale and
the unresolved-elevation marker colour. They have not been run in the remote
development environment because the Unity editor is unavailable there.

## Application and server checks to run locally

Use a test dataset and test server records. Record the Unity version, actual
Inspector settings, server version, feature IDs, and Console output.

| Scenario | Expected result |
| --- | --- |
| Load a POI with longitude/latitude outside the mesh and no altitude | Magenta unresolved preview stays at the supplied horizontal location. Saving does not invent an altitude. |
| Load a line or polygon with two-dimensional vertices, including one outside the mesh | Each vertex resolves its own elevation or remains flagged. A feature-level altitude or another vertex's surface estimate does not resolve the missing elevation. |
| Put a POI above and below a known surface, then move, rotate, scale, and mirror the map | Geographic coordinates remain stable and signed height stays consistent within the chosen measurement tolerance. A below-surface marker remains negative. |
| Disable the surface collider after obtaining a height and then move the marker | Height provenance records a retained or altitude-delta estimate rather than a new mesh measurement. |
| Draw a triangle in either direction | Both save with counterclockwise exterior coordinates and matching vertex heights. |
| Draw a bow tie or collinear polygon | Save reports the error and leaves the drawing available for correction or cancellation. |
| Create and move features offline, stop the app, restart, and reconnect | Pending edits remain on disk and the final content reaches the server. |
| Delete a synchronized feature offline, restart, and reconnect | The hidden deletion record survives and the corresponding server feature is deleted. |
| Make a second move while the first update is waiting for its reply | The first acknowledgement does not discard the second move. |
| Exhaust retries for one feature, then introduce an unrelated robot feature | The failed edit remains visible and the unrelated feature can arrive. |
| Edit one feature both locally and remotely before reconnecting | The conflict remains pending until explicitly resolved. Other features continue refreshing. |

For manual conflict resolution, select the object with
`GeoJsonSyncCoordinator`, enter the local feature ID in **Resolution Feature
Id**, and use its component context menu:

- **Sync/Resolve Selected Feature - Keep Local** retains the local content and
  queues it against the last observed remote version.
- **Sync/Resolve Selected Feature - Accept Server** adopts the observed server
  content, including a confirmed remote deletion.
- **Sync/Restore Pending Deletion** restores the retained local content.
- **Sync/Retry Pending Edits** retries eligible failed work.

## Server contract and measurement limits

The client sends a stable `Idempotency-Key` for each operation and an
`ar2_client_id` for client-created features. Verify whether the server honours
the key and preserves/returns the client ID. An uncertain create is compared
with the server before being resent. If its outcome cannot be established,
it stays visible as needing confirmation. Explicitly keeping the local
version can recreate it, so inspect the server before doing that.

The client compares remote content before writing. Preventing a second
writer from changing it between that comparison and the write requires
conditional-version support from the server. These checks do not establish
an exactly-once delivery or atomic multi-client conflict guarantee.

The validator checks individual site-local rings. It does not establish hole
containment or complete polygon topology, and it rejects polar or
antimeridian-crossing polygons.

The coordinator code defaults remain 30 seconds for general remote polling
and 4 seconds for the latest MQTT check. Existing scene Inspector values may
override these defaults. Neither these intervals nor the regression checks
are measured end-to-end latency or geographic-accuracy results.
