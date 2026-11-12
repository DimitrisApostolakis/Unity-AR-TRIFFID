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
| `Features/Model` | GeoJSON models, geometry validation and Mission Planner route classes | `GeoJsonDataModels`, `GeoJsonPolygonValidator`, `MissionPaths` |
| `Features/Persistence` | Atomic disk writes and durable edit state | `GeoJsonAtomicFile`, `GeoJsonSyncState` |
| `Features/Sync` | HTTP requests, incoming parsing and synchronization | `GeoJsonApiManager`, `GeoJsonPayloadParser`, `GeoJsonSyncCoordinator` |
| `Features/Spawning` | Existing JsonSpawner component and its focused implementations | `JsonSpawner` |
| `Features/Sensors` | UGV environmental readings: settings, HTTP, parsing and the alert monitor | `UgvSensorSettings`, `UgvSensorMonitor`, `UgvSensorApi` |
| `Features/Presentation` | Marker visuals, ground footprints, polygon fills, shared colours, events, data and category visibility | `FloatingIcon`, `MarkerFootprint`, `PolygonFill`, `SemanticPalette`, `PointData`, `GeoJsonVisibilityFilter` |
| `Interaction/Annotations` | Runtime strokes and persistence adapter | `RuntimeLine`, `JsonSpawnerPersistenceAdapter` |
| `Interaction/Input` | Controller/hand actions and menu interaction | `XRControllerLogger`, `XRControllerInputBridge` |
| `Interaction/Locomotion` | Movement | `FreeLocomotion` |
| `UI/Filters` | Filter controls | `CheckboxFilterGroup`, `CheckboxFilterHook` |
| `UI/InfoPanel` | Selected/hovered point information and its runtime styling | `PointInfoPanel`, `InfoPanelStyle` |
| `UI/Menu` | Modes, buttons, transitions, menu state and runtime cell layout | `MenuButtonController`, `MenuStateRestorer`, `MenuCellStyle` |
| `Diagnostics` | Consistent, tagged console logging | `TriffidLog` |

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

## Marker footprints

`MarkerFootprint` is added at runtime by `JsonSpawner.SetupIcon` to every marker
that has a `FloatingIcon`. It draws a flat ring, a centre dot and a tether at
the surface point below the icon's geographic anchor, because the bobbing,
billboarded sprite cannot state that point itself.

- It only reads `FloatingIcon.localMapPoint`, `PointData.heightAboveSurface`
  and `PulseEffect.IsPulsing`. It writes no marker state and no coordinates.
- The surface point is the anchor lowered by the height the info panel shows.
  With no known surface only a faint ring at the anchor is drawn.
- Visuals live under a scene-root `Marker Footprints` object, not under the
  map, so map clearing, `LineWidthController` and the experiment collectors do
  not enumerate them. They follow the icon's active state and lifetime.
- Ring colour groups the class: red (fire, smoke), amber (hole, mud, water,
  debris, barrel, barrier), green (citizen, animal), cyan (UGV), white (other).
- The ring thickens for the marker whose pulse is running and turns white
  while a marker is held. There is no spawn animation: markers are rebuilt on
  every server refresh, and an intro would replay each time.

### Icon pivots

The POI sprites in `Assets/Custom Scripts/Icons` use a custom pivot at their
lowest opaque pixel (pin tip, badge base, UGV wheels), measured per image. The
marker transform, and therefore the saved longitude, latitude, altitude and the
height shown in the info panel, is the point the icon visibly touches. No
coordinate code changed. `MarkerFootprint.AlignToSpritePivot` recentres the
prefab `BoxCollider` and the pulse ring on the drawn art at spawn; it is called
through `JsonSpawner.PrepareMarkerVisuals`. A new icon needs the same pivot set
in its import settings; a centre-pivoted sprite still works, with an estimated
tip.

Disable **Marker Footprints > Show Marker Footprints** on `JsonSpawner` to
reproduce performance captures recorded before footprints existed. Footprints
are created in Play mode only.

## Visual settings

`Assets/TRIFFID/Resources/TriffidVisualSettings.asset` is the single place to
tune the presentation layer: feature switches, the four group colours,
footprint ring/dot/tether sizes, polygon fill opacity, line rounding and the
menu cell layout. `TriffidVisualSettings.Current` loads it once per Play
session and falls back to built-in defaults when the asset is missing. Values
are read when a marker, polygon or menu is set up, so edit them outside Play.

The presentation scripts (`MarkerFootprint`, `PolygonFill`, `SemanticPalette`,
`MenuCellStyle`, `InfoPanelStyle`, `SpeechHintNames`) hold no tunable numbers
of their own. `SpeechHintNames` gives every button still on MRTK's generic
`select` keyword its own caption as keyword, so its hint reads `Say 'Fire'`.

## Mission Planner routes

The draw menu's Mission Planner button draws lines. A line is saved as a
GeoJSON `LineString` whose `class` is `responder_path` (Responders) or
`ugv_path` (UGV); `MissionPaths` owns the two names and `MissionPathColors`
their colours (`responderPathColor`, `ugvPathColor` in the visual settings).
A loaded route of either class is drawn in the same colour, whatever its
`marker_color`, and its vertices can be moved and deleted like any other
line. Any other `LineString` class keeps the previous behaviour.

## Keeping annotations above the map

`JsonSpawner.TryKeepAboveSurface` moves a position that lies below the
topmost surface on its geographic vertical straight up onto that surface.
It runs when a point or vertex is placed (`XRControllerLogger`) and when a
moved marker or vertex is released (`JsonSpawner.CompleteFeatureEdit`).
Features loaded from a file or the server are never moved by it.

## Polygon fills, shared colours and panel styling

`PolygonFill` is attached beside the `LineRenderer` of every polygon outline
(GeoJSON polygons in `JsonSpawner.SpawnLinePath`, drawn polygons in
`XRControllerLogger.CreateLineSession`). It triangulates the outline's current
world positions and draws them at 20 % of the outline colour. The mesh lives
under a scene-root `Polygon Fills` object and is rebuilt only when the outline
moves or changes colour. Each ring is filled on its own, so an inner ring
(hole) is filled too. `PolygonFill.StyleOutline` rounds the joins and ends of
all annotation lines. Fills need the always-included `Sprites/Default` shader;
without it polygons keep their outline only.

`SemanticPalette` is the single source of group colours. `MarkerFootprint`
rings, the info panel title and the filter/spawn/polygon-class captions use it,
so the menus act as the legend for the map.

`InfoPanelStyle` adds itself to every `PointInfoPanel` after scene load and
listens to `PointInfoPanel.MarkerPresented`. It finds `Class`, `IconImage` and
the slider fill by object name; renaming those objects only disables the
matching styling. It edits no prefab or scene.

`MenuCellStyle` lays out each icon cell of the filter and spawn menus at
runtime: whole icon above, caption below, checkbox as a corner badge, and a
fade for a class that is switched off. It also orders the info panel action
bar as Previous, Next, Delete. Targets are found by object name (`Checkbox`,
`UIButtonSpriteIcon`, `Label`, `BtnPrev`, `BtnNext`, `BtnDelete`).

## Notifications

`TriffidNotification` shows one card from the prefab `TRIFFID Notification`
in `Assets/TRIFFID/Resources`. There are three kinds (`ShowError`,
`ShowWarning`, `ShowSuccess`) and two placements:

- normal: in the line of sight; an error shakes as it arrives;
- quiet (`ShowQuiet`): smaller, low and to the right, no shake.

Only one card is visible at a time; the others wait in line and follow one by
one, so cards never cover each other. Normal messages are shown before quiet
ones, and a normal message makes a visible quiet card step aside (it returns
afterwards). A message identical to the visible or to a waiting one is not
added again. `maxWaiting` on the prefab limits the line.

Every card vanishes when its timer runs out. Timing and both placements are
fields on the prefab's component.

## UGV sensor readings and alerts

The API offers the limits (`/thresholds`) and the recorded alerts (`/alerts`)
of four readings. It has no endpoint for the current value of a reading, so
the app shows the last alert of each reading and how old it is.

| Script | Responsibility |
| --- | --- |
| `UgvSensorSettings` | Asset `Resources/UgvSensorSettings.asset`: addresses, pace, the list of readings (key, name, unit, scale, decimals), levels, colours, card and strip options |
| `UgvSensorApi` | The GET requests and their addresses |
| `UgvSensorPayloadParser`, `UgvSensorAlert`, `UgvSensorState` | The two answers and the state kept per reading |
| `UgvSensorMonitor` | Creates itself at Play, polls, raises `Changed` and `AlertsArrived` |
| `UgvSensorText` | One way of writing values, limits, ages, times and positions |
| `UgvSensorAlertNotice` (`UI/Notifications`) | Card for a new alert |
| `UgvSensorMenuStrip` (`UI/Menu`) | Row of readings at the top of the main menu |

- **New alerts are found by id.** The highest id seen is stored in
  `PlayerPrefs`, so an alert recorded while the app was closed is announced at
  the next start and none is announced twice. Alerts that arrive together
  each get a card, oldest first, up to `maxAlertCardsPerCheck`; beyond that
  only the newest are shown and the last card says `+N more`. The
  console line names both ids: `New alert 159 (temp); the previous alert was 158.`
  **Forget Last Seen Alert** in the monitor's context menu clears the stored id.
- **Levels and colours:** green, no alert on record; red, an alert younger
  than `activeAlertSeconds`; amber, an older alert; grey, no answer yet.
- **The strip edits no prefab.** It finds `Menu Origin` by name, adds its
  height to the top padding of that object's layout group and lifts the menu
  by half of it, so the grab bar below stays in place.
- **Testing without the real API:** switch on `useLocalServer` in the settings
  asset, run `python LocalScripts/local_geojson_server.py`, press Play, then
  record alerts with `python LocalScripts/local_sensor_alerts.py temp 31.5`
  (`--list` shows what is recorded, `--no-gps` records one without a
  position). The local alerts are kept in `LocalScripts/local_sensor_alerts.json`.
  `python LocalScripts/demo_sensor_alerts.py` walks through every case (below
  the limit, single, no GPS, several at once, repeated) and says what the app
  should show at each step.
  The last seen id is stored per address, so the real and the local server do
  not disturb each other.
- **Another reading** is one more entry in the settings list. **Another
  listener** subscribes to the two monitor events; the monitor shows nothing
  itself.

## Polygon titles and polygon icons

The corners of a Safe or Unsafe polygon keep the class `safe` / `unsafe`, both
when drawn and when rebuilt from GeoJSON, so the info panel titles them
`SAFE ZONE` / `UNSAFE ZONE`. Corners of any other line are classed `Drawing`
and titled by colour.

A polygon whose class has an icon prefab gets that icon at its centre. The
icon can be dragged only within the polygon's outer outline
(`JsonSpawner.KeepIconInsidePolygon`, called by `FloatingIcon` while held;
geometry in `PolygonContainment`). It stays where it is dropped, and returns
there after a refresh as long as that place is still inside. The place is
kept for the session only: GeoJSON has no field for it, so nothing is saved or
sent. Inner rings (holes) are not excluded.

## Walk mode and annotation colliders

`MenuButtonController` keeps annotation colliders enabled for the whole of
Walk mode, as well as in Info and Draw. Leaving Info or Draw while walking
keeps them on; leaving Walk turns them off unless Info or Draw is still open.

## Alignment and path ownership

The alignment components cooperate rather than representing interchangeable
copies:

1. `ColmapGaussianRotationAligner` reads the transform and applies rotation to
   the splat renderer. It preserves position and scale.
2. `MapColliderSync` binds the dedicated mesh directly below that same renderer.
   The renderer owns the complete display transform, including negative scale.
   There are no independent position/scale copy sources or Euler corrections.
   `OriginalPly` keeps mesh local position/rotation at zero and scale at one.
   `AlreadyMirroredX` applies a local X reflection only to undo a known import
   conversion. Following maintains the binding without rebuilding mesh geometry.
   One post-rotation sync is sufficient: parent changes are inherited immediately.
3. `MeshPathJsonLoader` loads and caches the collision mesh. It does not replace
   either alignment role.

### Updating an existing scene

Keep the Gaussian renderer's required local scale (for example `(-1, 1, 1)`).
On the existing `MapColliderSync`, assign that renderer and a **separate mesh
object**, choose `OriginalPly` for the Python converter's unchanged XYZ output,
then click **Match Gaussian Renderer**. The Inspector action records hierarchy
and transform Undo. Source/target object references keep their serialized field
names and the component GUID is unchanged. The hidden `MapTransform` reference
is retained for compatibility but no longer controls alignment. Old Euler
offsets and position/scale copy flags are deliberately superseded.

The mesh must not be the renderer or an ancestor of it. The follower rejects
these setups without changing the hierarchy. If the mesh importer baked an X
reflection into vertex coordinates, choose `AlreadyMirroredX`; do not also
negate mesh scale by hand. Import conversion cannot be inferred reliably from
symmetrical bounds. Geographic consumers must use the same **raw COLMAP frame**
as the renderer (or an identity child), not the child used to compensate an
importer's axis conversion. GeoJSON remains `[longitude, latitude, altitude]`.

Application scenes are not tracked in this branch, so private local scenes are
not rewritten. Existing assigned components bind at runtime; the Match button
applies the same arrangement in Edit Mode. Disable the component to stop both
automatic and aligner-triggered re-binding. An already-parented mesh continues
to inherit its parent; disabling the component does not freeze or detach it.
Keep map move/scale/reset controls on the common Map Root above the renderer,
not on its collider child. For imported meshes, Read/Write must be enabled when
required by collider cooking (including shear, or a convex collider under
negative scale); runtime-created OBJ meshes are initially readable.

Run **Tools > Experiments > Check Renderer Collider Alignment** for native
Unity checks of signed/nonuniform ancestor transforms, repeated matching,
map manipulation, raw-versus-imported axes, cycle guards, disabled followers,
and a real raycast against a reflected readable MeshCollider. Fixtures are
temporary; existing project objects are not edited. These native checks must
be run in Unity and are not represented by the Python regression suite.

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
| `JsonSpawner.cs` | Serialized setup, shared state, lifecycle, telemetry and display helpers |
| `JsonSpawner.Persistence.cs` | Save scheduling and atomic commits of existing geographic feature data |
| `JsonSpawner.FeatureEdits.cs` | Explicit vertex edit start/completion/cancellation and geographic updates |
| `JsonSpawner.Coordinates.cs` | Geographic conversion, transform loading and path validation |
| `JsonSpawner.Surface.cs` | Surface placement, altitude and height metadata |
| `JsonSpawner.Rendering.cs` | Geometry spawning, materials, cleanup, collider visibility and filters |
| `JsonSpawner.Editing.cs` | Add/delete operations and runtime node registration |
| `JsonSpawner.PolygonIcons.cs` | Keeps a dragged polygon icon inside its polygon and remembers where it was dropped |

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

## Console logging

Log through `TriffidLog` instead of calling `Debug.Log*` directly:

```csharp
TriffidLog.Info("JsonSpawner", $"Added {className} point {id} and saved.", this);
TriffidLog.Warning("MapReset", "Map target is missing; map reset cannot run.", this);
TriffidLog.Verbose("PointInfoPanel", $"Showing marker {id}.");
```

- Every message is tagged `[TRIFFID][Source]`, where `Source` is the class
  name. In the Editor console the tag is bold and coloured per source. Players
  get plain text, so Quest output can be filtered with
  `adb logcat -s Unity | grep "\[TRIFFID\]"`.
- Pass the component as the context so a click in the console selects it.
- Say what happened and what it affects, e.g. "X is missing; Y is unavailable."
- Use `Verbose` for per-marker, per-frame or per-state detail. It is hidden by
  default. Enable it with **Tools > TRIFFID > Verbose Logging** in the Editor or
  the `TRIFFID_VERBOSE_LOGS` scripting define in a player build.
- Use `TriffidLog.Fields(("Label", value), ...)` for multi-line reports. Keep
  the first line a one-line summary.
- Machine-read output keeps its exact text: the `PASS`/`FAIL` lines of the
  `Editor/Checks` runners and the `[SyncTiming]`/`[SaveTiming]` tokens.
  Caught exceptions still go to `Debug.LogException`.

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

