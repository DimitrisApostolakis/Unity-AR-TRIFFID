# Code and Inspector guide

The application scripts live in `projects/TRIFFID/Assets/TRIFFID`.
The renderer package stays in `package`, with its existing assembly boundary.
No application assembly definitions, namespaces or component names were changed.

## Runtime modules

| Folder under `Assets/TRIFFID/Runtime` | Responsibility | Main entry points |
| --- | --- | --- |
| `Geospatial/Alignment` | Renderer rotation and collision-object alignment | `ColmapGaussianRotationAligner`, `MapColliderSync` |
| `Geospatial/Coordinates` | Geographic conversions and surface queries | `GeoUtils`, `GeographicSurfaceQuery` |
| `Mapping/Loading` | Mesh loading/cache and legacy shared-config lookup | `MeshPathJsonLoader`, `StreamingAssetsPathResolver` |
| `Mapping/Manipulation` | Map reset, scaling, widths and camera background | `MapReset`, `ScaleChanger`, `LineWidthController` |
| `Features/Model` | GeoJSON models and geometry validation | `GeoJsonDataModels`, `GeoJsonPolygonValidator` |
| `Features/Persistence` | Atomic disk writes and durable edit state | `GeoJsonAtomicFile`, `GeoJsonSyncState` |
| `Features/Sync` | HTTP requests, incoming parsing and synchronization | `GeoJsonApiManager`, `GeoJsonPayloadParser`, `GeoJsonSyncCoordinator` |
| `Features/Spawning` | Existing JsonSpawner component and its focused implementations | `JsonSpawner` |
| `Features/Presentation` | Marker visuals, events, data and category visibility | `FloatingIcon`, `PointData`, `GeoJsonVisibilityFilter` |
| `Interaction/Annotations` | Runtime strokes and persistence adapter | `RuntimeLine`, `JsonSpawnerPersistenceAdapter` |
| `Interaction/Input` | Controller/hand actions and menu interaction | `XRControllerLogger`, `XRControllerInputBridge` |
| `Interaction/Locomotion` | Movement | `FreeLocomotion` |
| `UI/Filters` | Filter controls | `CheckboxFilterGroup`, `CheckboxFilterHook` |
| `UI/InfoPanel` | Selected/hovered point information | `PointInfoPanel` |
| `UI/Menu` | Modes, buttons, transitions and menu state | `MenuButtonController`, `MenuStateRestorer` |

`Editor/Inspectors` contains the shared Inspector presentation and its component
registrations. `Editor/Checks` contains the existing Unity regression menus.
Editor code remains under an `Editor` directory and is excluded from players.

## Inspector workflow

The application components use collapsible sections based on their existing
`Header` attributes. The first section is open initially. Foldout choices persist
for the current Editor session. **Expand all**, **Collapse all**, and **Find setting**
help navigate larger components. Search matches section titles, top-level field
names and tooltips; matching sections open while searching. Lists and nested
objects retain Unity's normal property UI and their own foldouts.

Runtime diagnostic counters are displayed read-only. Existing settings, ranges,
tooltips, UnityEvents, object references and serialized field names are retained.
Multi-object editing still uses `SerializedObject`, including mixed values.
The mesh and COLMAP Inspectors retain their dedicated path-source controls and
validation/apply buttons. Context-menu actions remain available as before.

For a new MonoBehaviour Inspector, add a small registration inheriting
`TriffidGroupedEditor` in `TriffidComponentEditors.cs`. Add ordinary `Header` and
`Tooltip` attributes to the runtime fields. Do not duplicate configuration into
Editor-only fields: the shared editor reads the component's actual serialized
properties. Property drawers for lists and custom types continue to work.

## Alignment and path ownership

The alignment components cooperate rather than representing interchangeable
copies:

1. `ColmapGaussianRotationAligner` reads the transform and applies rotation to
   the splat renderer. It preserves position and scale.
2. It can call `MapColliderSync`, which aligns the collision object using the
   existing map/renderer references, rotation offset and copy options.
3. `MeshPathJsonLoader` loads and caches the collision mesh. It does not replace
   either alignment role.

The mesh loader and COLMAP aligner already share
`package/Runtime/ProjectPathResolver.cs`. That resolver also serves the Gaussian
renderer package. Keep it in the package assembly so the renderer does not depend
on application scripts. `StreamingAssetsPathResolver` retains the existing
JsonSpawner lookup behavior; it returns a configured value that JsonSpawner then
resolves using its established rules. These paths were not silently unified,
since changing their roots or interpretation could change existing scenes.

## JsonSpawner source layout

There is still **one JsonSpawner scene component**. Its original script GUID,
serialized fields, state and public methods are preserved. Partial files compile
into that same class; do not attach them as additional components.

| File in `Features/Spawning` | Responsibility |
| --- | --- |
| `JsonSpawner.cs` | Serialized setup, shared state, lifecycle, telemetry and world-state tracking |
| `JsonSpawner.Persistence.cs` | Save scheduling, atomic commits and GeoJSON normalization |
| `JsonSpawner.Coordinates.cs` | Geographic conversion, transform loading and path validation |
| `JsonSpawner.Surface.cs` | Surface placement, altitude and height metadata |
| `JsonSpawner.Rendering.cs` | Geometry spawning, materials, cleanup, collider visibility and filters |
| `JsonSpawner.Editing.cs` | Add/delete operations and runtime node registration |

The extraction moves existing member bodies verbatim. It introduces no second
save queue, refresh loop, coordinate system or runtime manager.

## Python tools and data

Run Python tools from the repository root:

```bat
python LocalScripts/local_geojson_server.py
python LocalScripts/test_ar2_offline_sync.py
python LocalScripts/test_ar2_offline_sync.py --resume
python -m unittest discover -s LocalScripts/Tests -p "test_*.py" -v
```

All repository Python sources are under `LocalScripts`, including their tests in
`LocalScripts/Tests`. The shell runner stays at `scripts/test_ar2_core.sh`.
The local server defaults to the database at
`LocalScripts/local_features.json`, independently of the working
directory. Explicit `--db` paths are still supported. The outage test launches
the relocated server and uses that same database.

The database now lives beside the Python server in the repository-root
`LocalScripts` folder. Database contents, Unity GeoJSON save locations, and existing
icon/prefab paths are preserved. Update any external launch shortcuts that still
pass the previous `projects/TRIFFID/LocalScripts/local_features.json` path.

If your tracked database has local changes, stop the server before pulling the
move. Back up the old file outside its tracked path, restore only that old tracked
path, pull, and copy your backup into `LocalScripts/local_features.json`. Keep the
backup until you have checked the server data. Do not restore Unity's separate
GeoJSON save or pending-edit state as part of this database move.

## Adding functionality

- New feature properties belong in the model and parsing layers. Render them
  through the existing spawning/presentation flow.
- New HTTP operations belong in `GeoJsonApiManager`; scheduling, retries and
  observer acknowledgements belong in `GeoJsonSyncCoordinator`.
- Persist edits through the existing outbox and atomic file owner. Do not start
  a separate writer or announce delivery before the server confirms it.
- New annotation input should reuse the annotation persistence adapter and
  surface query helpers.
- Keep alignment, mesh loading and map manipulation independent. Reuse the
  shared path resolver when adding another package-aware loader.

## Migration and verification

Close Unity before pulling this reorganization, then reopen it and let assets
import. Script files move together with their original `.meta` files, so scene,
prefab, UnityEvent and component references continue to use the same GUIDs.
No scenes or prefabs require manual rewiring. New directories and partial files
have their own metadata. Existing serialized names and defaults are unchanged.

The headless C# runner uses the new source locations. Python checks include actual
local HTTP CRUD and restart. The mechanical migration is also checked for
preserved GUIDs and unchanged component members. A native Unity compilation and
scene smoke test are still required locally: open the demo, check the new
Inspectors, manipulate the map, edit a point/line/polygon, check filters and hover,
and repeat the guided outage test.
