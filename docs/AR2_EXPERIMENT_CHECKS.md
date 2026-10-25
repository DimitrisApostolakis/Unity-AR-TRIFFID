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

The verified result is **63 core checks, 19 coordinator checks, 12 mesh
loader checks, and 20 interaction checks passed**. The mesh checks compile the production loader with
engine test doubles and exercise cache reuse, invalidation, explicit reload,
collider bindings, and runtime/imported-mesh ownership.
The runner compiles the production data models, outbox, polygon validator,
atomic writer, payload parser, and synchronization coordinator with compiler
warnings treated as errors. Coordinator checks use simulated scheduling,
transport, and local-file ownership. They do not execute Unity physics,
the complete application, or a real server.

The interaction checks compile the production information panel, marker event
manager, runtime stroke, and surface query with engine doubles in a runtime library, then compile
the checks in a separate assembly to catch Editor/runtime visibility mistakes.
They verify collider-state
restoration on hits, misses, invalid rays and exceptions, stable panel selection,
explicit navigation, feature/node matching across server rebuilds, deletion,
and displayed height/status strings. They do not validate native PhysX queries,
XR inputs, TMP rendering or the actual scene layout.

## Height queries and information-panel selection

Map manipulation can disable the reference collider for interaction. A surface
query now temporarily enables that configured collider synchronously, synchronizes
its transform, performs its raycasts, and restores the disabled state in a
`finally` block. It does not replace the mesh or activate the mesh-loading
behaviour. The existing geographic vertical and signed-height calculation are
unchanged. The separately configured inactive-object activation policy still
applies; a query does not activate an inactive object by itself.

Unavailable elevation or height displays `-`. Estimated height has its own
second line, with `(Estimated)` below the numeric value. The panel displays the
class and source, for example `ANIMAL` and `GROUND STATION`. Synchronization
status stays in the synchronization state and diagnostics rather than appearing
in the point information panel.

Hover events and metadata changes for another marker no longer select it.
The panel follows explicit marker selection and previous/next navigation.
Server rebuilds retain the selected feature ID and its node ordinal, so
reordered feature collections do not send the panel back to the first point.
Removing a different marker preserves the current selection. If the selected
feature disappears in a server rebuild, the panel clears until another explicit
selection rather than showing an unrelated feature.

## Telemetry and drawing ownership

A GeoJSON rebuild now clears only its tracked annotations and saved runtime
strokes. It preserves the latest-MQTT marker's GameObject and re-registers its
panel entry. Previously the broad child cleanup destroyed the telemetry marker,
which reappeared at the next MQTT poll even when its coordinates were unchanged.

Drawing strokes register with the spawner and own their sibling vertices and
temporary material. An unfinished stroke survives a rebuild, and the coordinator
waits while it has draft vertices. Finishing or cancelling releases that wait.
Destroying a saved stroke or cancelling a draft also releases its owned vertices,
preventing orphan dots. A full explicit Clear Spawned Objects still clears all
runtime content.

Pointer placement uses the same configured reference surface and temporary
collider enabling as height queries. It no longer depends on a disabled global
physics target, or on hitting a marker instead of the reference surface. Switching
between line and polygon tools first commits the previous geometry. Invalid
drawings stay editable. Keyboard Enter finalizes, Backspace undoes a vertex,
and Escape cancels the unfinished annotation. `CancelCurrentAnnotation` is also
available to bind to an XR UI control.

## POI filters across saves and server refreshes

Visibility choices are now held in a separate runtime filter model rather than
only changing the current GameObjects. Rebuilding saved/server GeoJSON reapplies
those choices, and incoming or locally created POIs inherit the active filters.
Choices survive annotation cleanup during the current session. Filter state is
not written into the GeoJSON or sent to the server, and hiding a POI does not
delete its geographic data. Existing category/class matching and plural aliases
are retained. The native filter check rebuilds marker mappings twice and verifies
that hidden animals stay hidden until explicitly enabled again.

## Server authority and offline backup

The server is authoritative for features already synchronized with it. A
successful complete server snapshot removes deleted features from the local
file and the map, including their pending edits and any conflicts retained by
the former policy. When both clients changed a feature, the observed server
version wins. A local edit can still upload when the server retains the edit's
original baseline. A legacy snapshot without synchronization metadata is a
backup to reconcile with the server, not a source for recreating missing records.

Connection failures, timeouts, and invalid collections retain the local backup.
After a failed fetch, the coordinator retries a full comparison at the next
remote polling deadline even if observer-status flags are zero. Local annotation
creates that have never reached the server stay queued for upload. Creates
whose outcome or server identity is uncertain retain their reconciliation
state so a lost response does not cause duplicate uploads.

The Unity context menu **Sync/Refresh From Server** now immediately logs its
request and warns if the component is disabled. With **Verbose Logs** enabled,
a successful comparison logs server/local feature counts, removed features,
and remaining pending operations. A failed fetch reports its error and that
the local backup was retained. Finish any active marker manipulation and local
save before expecting the comparison to run.

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
4. Inspect the Console. Expected results are 63 passing core checks and
   **48 passing Unity checks**, with no failing checks.

The Unity checks create and remove temporary objects. They check signed
offsets of -2, 0, and +2 metres, out-of-mesh searches, map translation and
rotation, display scales of 0.01 and 3, mirrored scale, and a nonuniformly
scaled hierarchy. They also check rejection of invalid calibration scale and
the unresolved-elevation marker colour. The same 20 surface cases also run with
the collider initially disabled and check that it stays disabled after the
query, including misses. An additional panel check verifies selection through
background updates, rebuilding and server deletion. Further native checks cover
pointer placement on a disabled mesh, telemetry/draft preservation and saved-stroke
cleanup, and line/polygon tool switching and cancellation. They have not been run in the remote
development environment because the Unity editor is unavailable there.

The additional mesh-cache check loads a temporary OBJ, repeatedly invokes the
loader's enable path, verifies mesh identity and collider assignment counts,
performs a native surface raycast, and checks explicit reload and cleanup.
Run the updated menu check locally after pulling the cache optimization.

## Mesh reload pause identified in the capture

`ProfilerCaptures/TRIFFID-AR_2026-10-25_01-50-04.data` contains 402 decoded
frames. Its slowest capture-relative frame is 166 at 1,575.983 ms. Ten non-overlapping
`MeshPathJsonLoader.OnEnable` samples account for 1,515.759 ms of that frame.
Their call paths descend from `GeoJsonSyncCoordinator.Synchronize` through
`GameObject.Activate`. The render thread waits for a signal for 1,534.438 ms
in the same frame. Frame 167 contains another 143.714 ms mesh enable/load in
the separate `LatestMqttLoop` path.

The save preparation in frame 89 totals approximately 1.265 ms across the
instrumented stages. The completion log in frame 91 takes 0.286 ms. These
samples identify repeated collider-mesh loading as the dominant captured
pause, rather than the measured JSON preparation or completion log.

`JsonSpawner.PrepareSurfaceForSnap` temporarily activates the configured
surface for queries. Before the fix, `MeshPathJsonLoader.OnEnable` loaded and
applied the mesh on every activation. Annotation respawning triggered this
repeatedly. The loader now caches its mesh, checks source metadata (and asset
dependency hashes in the Editor), and avoids reassigning an unchanged
collider. The existing explicit **Load And Apply Mesh** action forces a reload.
Externally loaded meshes are owned and released by the loader; imported
Unity mesh assets are not destroyed by it.

The optimization is confined to the mesh-loading module. Surface queries
still activate/synchronize the collider as before, and synchronization can
still refresh annotations. Incremental annotation updates remain a separate
optimization opportunity; this change targets the measured bottleneck.

After pulling, watch **Runtime Mesh Diagnostics** on `MeshPathJsonLoader`:
**Mesh Load Count** and **Collider Assignment Count** should remain stable
when unchanged geometry is temporarily enabled for repeated height queries.
**Mesh Cache Hit Count** can increase. A source change or explicit reload
should increase the load count. Repeat the actual-scene capture to measure
the resulting frame-time improvement.

The binary was decoded with Unity Japan's ProfilerReader library, carrying
its marker dictionary across frames. Timing samples and call paths decoded
through the end of the capture. That library reported unsupported memory and
render-stat counters for this Unity version; those counter values were not
used to infer the cause. Native capture SHA-256:
`a03afa47b3f3cb09cb80444865d6f1ee505c07727c6d17fddeda6e85fa92168d`.

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
| Create and move features offline, stop the app, restart, and reconnect | Pending edits remain on disk. New creates upload, and edits upload if their server baseline has not changed. |
| Delete a synchronized feature offline, restart, and reconnect | The hidden deletion record survives. It deletes an unchanged server feature or is cancelled if the server independently changed that feature. |
| Make a second move while the first update is waiting for its reply | The first acknowledgement does not discard the second move. |
| Exhaust retries for one feature, then introduce an unrelated robot feature | The failed edit remains visible and the unrelated feature can arrive. |
| Edit one feature both locally and remotely before reconnecting | The server version replaces the pending local version automatically. Other features continue refreshing. |
| Delete every server feature while AR has pending edits or old deletion conflicts | The next successful full fetch clears those features and their pending edits from disk and the map, without re-uploading them. |
| Disconnect AR, delete server features, restart AR offline, then reconnect | The backup remains visible offline. Reconnection applies the deletions, and a subsequent restart does not restore them. |
| Return an invalid or incomplete response instead of a valid empty FeatureCollection | The local backup and visible map remain intact. |
| Inspect a negative mesh height in the information panel | It displays a signed number such as `-0.40 m`, without a below-surface suffix. |
| Disable the surface MeshCollider while manipulating the map, then query an annotation's height | A valid mesh intersection still resolves the height. The MeshCollider remains disabled after the query. |
| Leave one marker selected while hovering others, receiving MQTT updates, or applying server refreshes | The panel retains the selected feature and node, unless that feature was removed. |
| Inspect an estimated height and a pending marker | The estimate label appears below the number. Only the class and source are shown above the confidence bar. Missing height/elevation is `-`. |
| Refresh server annotations while the latest UGV telemetry is unchanged | The same telemetry GameObject remains visible without waiting for the next MQTT poll. |
| Start a line or polygon, wait through a remote polling deadline, then finish or cancel it | Vertices and the draft stroke remain together. Synchronization resumes afterwards. |
| Cancel a draft or remove a saved drawing through server refresh | The stroke and its owned vertices disappear together without leaving orphan dots. |
| Draw with the reference MeshCollider disabled, then switch line/polygon tools | Pointer placement still hits the configured mesh. The previous valid drawing finishes before a new geometry type starts. |
| Hide a POI class, save/edit GeoJSON or receive a server refresh, and introduce another POI of that class | Existing, rebuilt and incoming POIs remain hidden. Re-enabling that class restores its POIs without changing other filter choices. |

Server edits and deletions do not require manual conflict resolution. For
unresolved create identities or duplicate client IDs, select the object with
`GeoJsonSyncCoordinator`, enter the local feature ID in **Resolution Feature
Id**, and use its component context menu:

- **Sync/Resolve Selected Feature - Keep Local** retains the local content and
  queues it against the last observed remote version.
- **Sync/Resolve Selected Feature - Accept Server** adopts the observed server
  content for that unresolved identity.
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
