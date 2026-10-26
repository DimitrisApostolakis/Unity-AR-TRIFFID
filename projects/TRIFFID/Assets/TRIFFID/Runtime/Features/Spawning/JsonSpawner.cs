using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;

public partial class JsonSpawner : MonoBehaviour
{
    private const string DefaultGeoJsonPath = "Assets/Coordinates/GeoJSON.json";
    private const string DefaultRelativeSavePath = "Coordinates/GeoJSON_updated.json";
    private const string LegacyCoordinatesChangesSuffix = "coordinates_changes.json";
    private const string LatestMqttFeatureId = "latest_mqtt_icon";
    private const string LatestMqttClassName = "ugv";

    [Header("GeoJSON / Transform File Paths")]
    [Tooltip("Source GeoJSON used when no saved runtime file is available. Relative paths are resolved from the project root.")]
    public string geoJsonPath = DefaultGeoJsonPath; 
    [Tooltip("COLMAP-to-ENU transform configuration JSON used for coordinate conversion.")]
    public string transformJsonPath = "Assets/Coordinates/transform_colmap_to_enu.json";
    [Tooltip("Primary runtime GeoJSON save file. Relative paths use the save location selected below.")]
    public string saveFilePath = DefaultRelativeSavePath;

    [Header("Shared Transform Path Config")]
    [SerializeField] private bool useSharedTransformPathJson = true;
    [SerializeField] private string sharedTransformPathsJsonFile = "project_paths.json";
    [SerializeField] private string transformPathJsonKey = "json_spawner_transform_path";

    [Header("Save Location")]
    [Tooltip("When enabled, relative save paths resolve inside the Unity project folder instead of Application.persistentDataPath.")]
    [SerializeField] private bool saveInsideProjectFolder;

    [Header("Map Parent (Optional)")]
    [Tooltip("Optional parent transform for spawned map annotations. The JsonSpawner transform is used when this is not assigned.")]
    public Transform mapTransform;

    [Header("Geometry Prefabs and Visuals")]
    [Tooltip("Prefab used for line and polygon vertices and as the fallback geometry node prefab.")]
    public GameObject nodePrefab; 
    [Tooltip("Optional source material copied for spawned lines. A fallback shader material is used when this is not assigned.")]
    public Material lineMaterial;
    [Min(0f)]
    [Tooltip("Width applied to spawned line and polygon renderers.")]
    public float lineThickness = 0.01f;
    [Tooltip("Fallback colors assigned to spawned geometry when the GeoJSON does not provide a specific color.")]
    public List<Color> colorPalette = new List<Color> { Color.cyan, Color.yellow, Color.magenta, Color.green, Color.red };

    [Header("Point Prefab Mapping")]
    [Tooltip("Maps GeoJSON class names to the prefabs used for single-point features.")]
    public List<PrefabEntry> prefabEntries;

    [Header("UI References")]
    [Tooltip("Optional information panel that receives registrations for spawned annotation markers.")]
    public PointInfoPanel infoPanel;

    [Header("Menu State")]
    [Tooltip("Optional menu controller used when an operation needs to respect the current interaction mode.")]
    [SerializeField] private MenuButtonController menuButtonController;

    [Header("Auto Save")]
    [Tooltip("Automatically writes dirty local annotation state after the configured interval.")]
    [SerializeField] private bool enableAutoSaveTimer = true;
    [Min(0f)]
    [Tooltip("Seconds between automatic save attempts while local annotation data is dirty.")]
    [SerializeField] private float autoSaveIntervalSeconds = 3f;

    [Header("Save Profiling")]
    [Tooltip("Adds stage durations to the existing save log and reports synchronization commits taking at least 16 ms.")]
    [SerializeField] private bool logSaveStageTimings = true;

    private sealed class SaveStageTimings
    {
        public double world, normalize, outbox, labels, serialize;
    }

    private static double MeasureSaveStage(string name, Action action)
    {
        long started = Stopwatch.GetTimestamp();
        UnityEngine.Profiling.Profiler.BeginSample(name);
        try { action(); }
        finally { UnityEngine.Profiling.Profiler.EndSample(); }
        return (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    }

    [Header("Runtime Collider State")]
    [Tooltip("Initial enabled state applied to colliders on spawned annotation objects.")]
    [SerializeField] private bool startWithCollidersEnabled;

    [Header("Surface Queries / Snap Fallback")]
    [Tooltip("When altitude is missing, attempts to raycast the annotation onto a configured map surface.")]
    [SerializeField] private bool enableSurfaceSnapFallback = true;
    [Tooltip("Physics layers considered by surface-height and surface-snap raycasts.")]
    [SerializeField] private LayerMask surfaceSnapMask = ~0;
    [Min(0f)]
    [Tooltip("Physical distance in metres above the estimated annotation position where a surface ray begins.")]
    [SerializeField] private float surfaceSnapRayStartOffset = 25f;
    [Min(0f)]
    [Tooltip("Physical distance in metres used by each surface raycast.")]
    [SerializeField] private float surfaceSnapRayDistance = 100f;
    [Tooltip("Offset in physical metres along ENU Up after a successful surface snap.")]
    [SerializeField] private float surfaceSnapOffset = 0.01f;
    [Tooltip("Additional offset in physical metres along ENU Up after surface snapping.")]
    [SerializeField] private float surfaceSnapLift = 0.03f;
    [Tooltip("Optional map surface object that may be activated temporarily for surface snapping.")]
    [SerializeField] private GameObject surfaceSnapTargetObject;
    [Tooltip("Optional behaviour that receives SyncToMap before surface-snap raycasts.")]
    [SerializeField] private MonoBehaviour surfaceSnapSyncBehaviour;
    [Tooltip("Temporarily activates the surface target when it is inactive and a snap is requested.")]
    [SerializeField] private bool autoActivateSurfaceForSnap = true;
    [Tooltip("Restores the surface target to its previous inactive state after a snap attempt.")]
    [SerializeField] private bool restoreSurfaceActiveStateAfterSnap = true;

    private Dictionary<string, GameObject> prefabDictionary;
    private List<GameObject> spawnedObjects = new List<GameObject>();
    private Root currentRootData;
    private TransformConfig transformData;
    private bool transformLoadWarningShown;
    private int dataVersion;
    private int lastSavedVersion;
    private bool saveInProgress;
    private bool saveQueued;
    private float autoSaveTimer;
    private readonly GeoJsonSaveSequence diskSaves = new GeoJsonSaveSequence();
    private object fileWriteLock => diskSaves.SyncRoot;
    private readonly object dataMutationLock = new object();
    private readonly HashSet<string> reservedFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    private List<LineData> activeLines = new List<LineData>();
    private readonly List<RuntimeLine> runtimeAnnotationStrokes = new List<RuntimeLine>();
    private readonly GeoJsonVisibilityFilter categoryVisibility = new GeoJsonVisibilityFilter();
    private readonly Dictionary<Transform, string> lineNodeColorHexByNode = new Dictionary<Transform, string>();
    private Dictionary<string, GameObject> prefabDictionaryNormalized;
    private readonly Dictionary<string, LineData> activeLinesByObjectName = new Dictionary<string, LineData>(StringComparer.Ordinal);
    private bool spawnInProgress;
    private bool spawnQueued;
    private bool collidersDesiredActive;
    private bool latestMqttAltitudeCached;
    private double latestMqttCachedAltitude;
    private string resolvedSaveFilePath;
    private Collider surfaceQueryCollider;

    private class LineData {
        public LineRenderer renderer;
        public List<Transform> nodes;
        public Material createdMaterial;
        public Feature parentFeature;
    }

    private class NodeMapping {
        public Transform node;
        public JArray coordArray;
        public Feature parentFeature;
        public bool isCentroid; 
        public Transform parentCentroid; 
    }

    private List<NodeMapping> nodeMappings = new List<NodeMapping>();

    public string LastValidationError { get; private set; }

    private void ReportValidationError(string error)
    {
        LastValidationError = error;
        Debug.LogWarning("[JsonSpawner] " + error);
        if (infoPanel != null) infoPanel.ShowAnnotationError(error);
    }

    private void UpdateSyncStatusLabels()
    {
        if (currentRootData == null || currentRootData.syncState == null) return;
        foreach (NodeMapping mapping in nodeMappings)
        {
            if (mapping.node == null) continue;
            PointData data = mapping.node.GetComponent<PointData>();
            if (data == null) continue;
            GeoJsonPendingEdit edit = GeoJsonOutbox.Find(currentRootData, data.pointID);
            data.syncStatus = edit == null ? string.Empty :
                edit.status == "conflict" ? "Conflict" : edit.status == "reconcile" ? "Needs confirmation" :
                edit.status == "failed" ? "Needs retry" : "Pending";
            data.NotifyDataChanged();
        }
    }

    private void Awake()
    {
        if (string.IsNullOrWhiteSpace(geoJsonPath))
            Debug.LogError("[JsonSpawner] GeoJSON input path is empty.", this);

        if (!useSharedTransformPathJson && string.IsNullOrWhiteSpace(transformJsonPath))
            Debug.LogError("[JsonSpawner] Transform config path is empty.", this);

        geoJsonPath = SanitizeGeoJsonPath(geoJsonPath);
        GetResolvedSaveFilePath();
        ValidateSavePathConfiguration(saveFilePath);
        collidersDesiredActive = startWithCollidersEnabled;

        prefabDictionary = new Dictionary<string, GameObject>();
        prefabDictionaryNormalized = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        if (prefabEntries == null)
        {
            Debug.LogWarning("[JsonSpawner] Prefab entries list is null. Class-specific prefab lookup is disabled.");
        }
        else
        {
            foreach (var entry in prefabEntries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.name) || entry.prefab == null)
                    continue;

                prefabDictionary[entry.name] = entry.prefab;

                string normalized = NormalizePrefabToken(entry.name);
                if (!string.IsNullOrEmpty(normalized))
                    prefabDictionaryNormalized[normalized] = entry.prefab;
            }
        }
    }

    private void Update()
    {
        if (!enableAutoSaveTimer)
            return;

        if (dataVersion <= lastSavedVersion || saveInProgress)
            return;

        autoSaveTimer += Time.deltaTime;
        if (autoSaveTimer >= Mathf.Max(0.5f, autoSaveIntervalSeconds))
        {
            autoSaveTimer = 0f;
            SaveCurrentStateToPersistentStorage();
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
            FlushPendingSaveSynchronously();
    }

    private void OnApplicationQuit()
    {
        FlushPendingSaveSynchronously();
    }

    [ContextMenu("1. Spawn From Json")]
    public void SpawnFromJson()
    {
        if (spawnInProgress)
        {
            spawnQueued = true;
            return;
        }

        spawnInProgress = true;
        try
        {
            if (!TryPreflightSpawnInputs(out TransformConfig loadedTransform, out Root loadedRoot, out string loadPath))
                return;

            if (infoPanel != null) infoPanel.BeginMarkerRefresh();
            try
            {
            ClearSpawnedObjects(true);
            transformData = loadedTransform;
            transformLoadWarningShown = false;
            currentRootData = loadedRoot;
            GeoJsonOutbox.Initialize(currentRootData, true);

            int colorIndex = 0;
            bool hasColorPalette = colorPalette != null && colorPalette.Count > 0;
            if (!hasColorPalette)
                Debug.LogWarning("[JsonSpawner] Color palette is null or empty. Falling back to cyan for spawned geometry.");

            foreach (Feature feature in currentRootData.features)
            {
                Color groupColor = hasColorPalette
                    ? colorPalette[colorIndex % colorPalette.Count]
                    : Color.cyan;

                if (!TryValidateFeatureForSpawn(feature, out string validationError))
                {
                    string featureId = feature?.id ?? feature?.properties?.id ?? $"index {colorIndex}";
                    Debug.LogWarning($"[JsonSpawner] Skipped malformed feature '{featureId}': {validationError}");
                    colorIndex++;
                    continue;
                }

                try
                {
                    ProcessGeometry(feature.geometry, feature, groupColor);
                }
                catch (Exception ex)
                {
                    string featureId = feature.id ?? feature.properties?.id ?? $"index {colorIndex}";
                    Debug.LogWarning($"[JsonSpawner] Skipped feature '{featureId}' after a spawn error: {ex.Message}");
                }

                colorIndex++;
            }
            Debug.Log($"[JsonSpawner] Spawned {nodeMappings.Count} coordinate nodes successfully.");

            // SetAllColliders(true);
            ApplyColliderStateToSpawnedObjects(collidersDesiredActive, false);
            ApplyCurrentCategoryFilters();
            UpdateSyncStatusLabels();

            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonSpawner] Failed to parse GeoJSON file '{loadPath}': {ex.Message}", this);
            }
            finally
            {
                if (infoPanel != null) infoPanel.EndMarkerRefresh();
            }
        }
        finally
        {
            spawnInProgress = false;
            if (spawnQueued)
            {
                spawnQueued = false;
                SpawnFromJson();
            }
        }
    }

    public bool SpawnLatestMqttIcon(MqttLatestResponse response)
    {
        if (response?.message == null || !response.message.latitude.HasValue || !response.message.longitude.HasValue)
        {
            Debug.LogWarning("[JsonSpawner] SpawnLatestMqttIcon skipped: invalid MQTT payload.");
            return false;
        }

        if (!IsFiniteNumber(response.message.latitude.Value) || !IsFiniteNumber(response.message.longitude.Value))
        {
            Debug.LogWarning("[JsonSpawner] SpawnLatestMqttIcon skipped: MQTT coordinates are not finite.");
            return false;
        }

        double originAltitude = GetTransformOriginAltitude();
        bool useCachedAltitude = latestMqttAltitudeCached;
        double altitude = useCachedAltitude ? latestMqttCachedAltitude : 0d;
        if (TryGetLatestMqttRuntimeMarker(out GameObject existingObject, out PointData existingData))
        {
            UpdateLatestMqttRuntimeMarker(existingObject, existingData, response, useCachedAltitude ? altitude : originAltitude, useCachedAltitude);
            return true;
        }

        JArray coords = useCachedAltitude
            ? new JArray { response.message.longitude.Value, response.message.latitude.Value, altitude }
            : new JArray { response.message.longitude.Value, response.message.latitude.Value };

        Feature feature = new Feature
        {
            type = "Feature",
            id = LatestMqttFeatureId,
            properties = new Properties
            {
                className = LatestMqttClassName,
                id = LatestMqttFeatureId,
                category = "telemetry",
                source = string.IsNullOrWhiteSpace(response.topic) ? "mqtt" : response.topic,
                confidence = 1f,
                altitude_m = useCachedAltitude ? (float?)altitude : null
            },
            geometry = new Geometry
            {
                type = "Point",
                coordinates = coords
            }
        };

        Transform spawned = SpawnPointNode(
            response.message.longitude.Value,
            response.message.latitude.Value,
            coords,
            feature,
            Color.cyan,
            false,
            null,
            false,
            useCachedAltitude ? altitude : originAltitude
        );

        if (spawned == null)
        {
            Debug.LogWarning("[JsonSpawner] SpawnLatestMqttIcon failed to create the icon.");
            return false;
        }

        PointData spawnedData = spawned.GetComponent<PointData>();
        if (spawnedData != null && !spawnedData.elevationUnresolved)
        {
            latestMqttCachedAltitude = spawnedData.altitude;
            latestMqttAltitudeCached = true;
        }

        spawned.gameObject.name = "LatestMqtt_" + LatestMqttFeatureId;

        if (spawnedData != null)
        {
            spawnedData.pointClass = LatestMqttClassName;
            spawnedData.category = "telemetry";
            spawnedData.source = string.IsNullOrWhiteSpace(response.topic) ? "mqtt" : response.topic;
            spawnedData.pointID = LatestMqttFeatureId;
            spawnedData.behavior = string.IsNullOrWhiteSpace(response.message.behavior) ? "STOPPED" : response.message.behavior;
        }

        EnsureHoverInteractable(spawned.gameObject);

        return true;
    }

    [ContextMenu("2. Save All To Json")]
    public void SaveToJson()
    {
        MarkDataDirty();
        SaveCurrentStateToPersistentStorage();
    }

    internal int LocalPersistenceVersion => dataVersion;

    internal bool HasLoadedFeatureCollection => currentRootData?.features != null;

    internal bool HasPendingLocalPersistence =>
        saveInProgress || saveQueued || dataVersion > lastSavedVersion;

    // Atomic replacement failures are reported. The saved copy is never truncated.

    private static bool TryExtractFirstCoordinateTriplet(JToken token, out double lon, out double lat, out double alt)
    {
        lon = 0d;
        lat = 0d;
        alt = 0d;

        if (token == null)
            return false;

        if (token is JArray arr)
        {
            if (arr.Count >= 2 &&
                TryGetDouble(arr[0], out lon) &&
                TryGetDouble(arr[1], out lat))
            {
                if (arr.Count >= 3 && TryGetDouble(arr[2], out double parsedAlt))
                    alt = parsedAlt;

                return true;
            }

            foreach (var child in arr)
            {
                if (TryExtractFirstCoordinateTriplet(child, out lon, out lat, out alt))
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetDouble(JToken token, out double value)
    {
        value = 0d;
        if (token == null)
            return false;

        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
        {
            value = token.Value<double>();
            return true;
        }

        if (token.Type == JTokenType.String)
            return double.TryParse(token.Value<string>(), out value);

        return false;
    }

    private static bool IsValidCoordinateSet(double lat, double lon, double alt)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt))
            return false;

        if (double.IsInfinity(lat) || double.IsInfinity(lon) || double.IsInfinity(alt))
            return false;

        if (Math.Abs(lat) < 1e-9 && Math.Abs(lon) < 1e-9 && Math.Abs(alt) < 1e-9)
            return false;

        return true;
    }

    private void RecalculateCentroidForNode(NodeMapping movedNodeMapping)
    {
        Transform centroidTransform = movedNodeMapping.parentCentroid;
        if (centroidTransform == null) return; 

        List<NodeMapping> siblingMappings = nodeMappings.FindAll(m => m.parentCentroid == centroidTransform && !m.isCentroid);
        if (siblingMappings.Count == 0) return;

        List<Transform> uniqueNodes = new List<Transform>();
        Vector3 sumPos = Vector3.zero;
        
        foreach (var sibling in siblingMappings)
        {
            if (!uniqueNodes.Contains(sibling.node))
            {
                uniqueNodes.Add(sibling.node);
                sumPos += sibling.node.position;
            }
        }
        
        Vector3 newCenter = sumPos / uniqueNodes.Count;
        centroidTransform.position = newCenter;
    }

    private void SyncWorldToJSON()
    {
        Transform parentRef = mapTransform != null ? mapTransform : transform;
        HashSet<Feature> vertexHeightFeatures = new HashSet<Feature>();

        foreach (var mapping in nodeMappings)
        {
            if (mapping == null || mapping.node == null || mapping.parentFeature == null)
                continue;

            PointData mappedData = mapping.node.GetComponent<PointData>();
            if (mappedData != null && mappedData.elevationUnresolved)
            {
                // The displayed altitude is only a preview. Preserve the original
                // geographic position and do not export an invented altitude.
                if (mapping.parentFeature.properties != null)
                {
                    mapping.parentFeature.properties.elevationStatus = "unresolved";
                    if (IsPointFeature(mapping.parentFeature)) mapping.parentFeature.properties.altitude_m = null;
                }
                if (!mapping.isCentroid && mapping.coordArray != null && IsCoordinateTuple(mapping.coordArray))
                    while (mapping.coordArray.Count > 2) mapping.coordArray.RemoveAt(mapping.coordArray.Count - 1);
                continue;
            }
            Vector3 localToMap = GetStableMapLocalPosition(mapping.node, parentRef);
            Vector3Double wgs84 = ColmapToWgs84(localToMap);

            if (!mapping.isCentroid && mapping.coordArray != null && mapping.coordArray.Count >= 2)
            {
                mapping.coordArray[0] = wgs84.lon;
                mapping.coordArray[1] = wgs84.lat;

                if (mapping.coordArray.Count >= 3)
                    mapping.coordArray[2] = wgs84.alt;
                else
                    mapping.coordArray.Add(wgs84.alt);
            }

            if (!mapping.isCentroid && mapping.parentFeature.properties != null)
            {
                mapping.parentFeature.properties.altitude_m = (float)wgs84.alt;

                if (IsPointFeature(mapping.parentFeature))
                {
                    PointData pointData = mapping.node.GetComponent<PointData>();
                    if (pointData != null) StorePointHeightMetadata(mapping.parentFeature, pointData);
                }
                else if (SupportsPerVertexHeight(mapping.parentFeature))
                {
                    vertexHeightFeatures.Add(mapping.parentFeature);
                }
            }

            SyncPointDataFromWgs(mapping.node, wgs84);
        }

        EnforceClosedPolygonRings();

        foreach (Feature feature in vertexHeightFeatures)
            UpdateFeatureHeightAboveSurfaceData(feature);
    }

    private static Vector3 GetStableMapLocalPosition(Transform node, Transform defaultMapTransform)
    {
        if (node == null)
            return Vector3.zero;

        FloatingIcon icon = node.GetComponent<FloatingIcon>();
        if (icon != null && icon.mainMap != null && !icon.IsBeingManipulated)
            return icon.localMapPoint;

        if (defaultMapTransform == null)
            return node.position;

        return defaultMapTransform.InverseTransformPoint(node.position);
    }

    private static void SyncPointDataFromWgs(Transform node, Vector3Double wgs)
    {
        if (node == null)
            return;

        PointData data = node.GetComponent<PointData>();
        if (data == null)
            return;

        bool changed = false;
        if (Math.Abs(data.longitude - wgs.lon) > 1e-9)
        {
            data.longitude = wgs.lon;
            changed = true;
        }

        if (Math.Abs(data.latitude - wgs.lat) > 1e-9)
        {
            data.latitude = wgs.lat;
            changed = true;
        }

        if (Math.Abs(data.altitude - wgs.alt) > 1e-6)
        {
            data.altitude = wgs.alt;
            changed = true;
        }

        if (changed)
            data.NotifyDataChanged();
    }

    private void EnforceClosedPolygonRings()
    {
        if (currentRootData == null || currentRootData.features == null) return;
        foreach (Feature feature in currentRootData.features)
        {
            string error;
            if (!GeoJsonPolygonValidator.TryNormalizeFeature(feature, out error))
                throw new InvalidDataException("Feature " + feature.id + ": " + error);
        }
        LastValidationError = null;
    }

    private static void EnsureRingClosed(JArray ring)
    {
        if (ring == null || ring.Count == 0)
            return;

        if (!(ring[0] is JArray first) || first.Count < 2)
            return;

        JArray closing = new JArray
        {
            first[0].Value<double>(),
            first[1].Value<double>(),
            first.Count >= 3 ? first[2].Value<double>() : 0d
        };

        if (!(ring[ring.Count - 1] is JArray last) || last.Count < 2)
        {
            ring.Add(closing);
            return;
        }

        bool alreadyClosed = Math.Abs(first[0].Value<double>() - last[0].Value<double>()) < 1e-10 &&
                             Math.Abs(first[1].Value<double>() - last[1].Value<double>()) < 1e-10;

        if (alreadyClosed)
        {
            // Keep the original coordinate array instance because nodeMappings
            // references it directly for live vertex updates.
            last.Clear();
            foreach (JToken value in closing)
                last.Add(value);
        }
        else
        {
            ring.Add(closing);
        }
    }

    private bool TryGetLatestMqttRuntimeMarker(out GameObject markerObject, out PointData markerData)
    {
        markerObject = null;
        markerData = null;

        for (int i = spawnedObjects.Count - 1; i >= 0; i--)
        {
            GameObject candidate = spawnedObjects[i];
            if (candidate == null)
            {
                spawnedObjects.RemoveAt(i);
                continue;
            }

            PointData data = candidate.GetComponent<PointData>();
            if (data == null)
                continue;

            if (string.Equals(data.pointID, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase))
            {
                markerObject = candidate;
                markerData = data;
                return true;
            }
        }

        return false;
    }

    private void UpdateLatestMqttRuntimeMarker(GameObject markerObject, PointData markerData, MqttLatestResponse response, double fallbackAltitude, bool useCachedAltitude)
    {
        if (markerObject == null || markerData == null || response?.message == null)
            return;

        Transform parentRef = mapTransform != null ? mapTransform : transform;

        double altitude = useCachedAltitude ? latestMqttCachedAltitude : fallbackAltitude;
        Vector3 targetPosition = GetColmapPosition(response.message.longitude.Value, response.message.latitude.Value, altitude);

        bool elevationResolved = false;
        if (enableSurfaceSnapFallback)
        {
            Vector3 worldPosition = parentRef.TransformPoint(targetPosition);
            if (TrySnapToSurface(worldPosition, parentRef, out Vector3 snappedWorldPos))
            {
                markerObject.transform.position = snappedWorldPos;
                Vector3 snappedLocalPos = parentRef.InverseTransformPoint(snappedWorldPos);
                JsonSpawner.Vector3Double snappedWgs = ColmapToWgs84(snappedLocalPos);
                altitude = snappedWgs.alt;
                targetPosition = GetColmapPosition(response.message.longitude.Value, response.message.latitude.Value, altitude);
                elevationResolved = true;
                latestMqttCachedAltitude = altitude;
                latestMqttAltitudeCached = true;
            }
        }

        markerObject.transform.position = parentRef.TransformPoint(targetPosition);

        FloatingIcon floatingIcon = markerObject.GetComponent<FloatingIcon>();
        if (floatingIcon != null)
        {
            floatingIcon.mainMap = parentRef;
            floatingIcon.localMapPoint = targetPosition;
        }

        markerData.latitude = response.message.latitude.Value;
        markerData.longitude = response.message.longitude.Value;
        markerData.altitude = altitude;
        markerData.SetElevationUnresolved(!elevationResolved);
        markerData.confidence = 1f;
        markerData.pointClass = LatestMqttClassName;
        markerData.category = "telemetry";
        markerData.source = string.IsNullOrWhiteSpace(response.topic) ? "mqtt" : response.topic;
        markerData.pointID = LatestMqttFeatureId;
        markerData.behavior = string.IsNullOrWhiteSpace(response.message.behavior) ? "STOPPED" : response.message.behavior;

        RefreshHeightAboveSurface(markerObject.transform, null, markerData);

        latestMqttCachedAltitude = altitude;
        latestMqttAltitudeCached = true;

        markerData.NotifyDataChanged();
        MarkerEventManager.RaiseMarkerMoved(markerData);
    }

    public struct Vector3Double { public double lon, lat, alt, x, y, z; }

    private void OnValidate()
    {
        geoJsonPath = SanitizeGeoJsonPath(geoJsonPath);
        saveFilePath = SanitizeSaveFilePath(saveFilePath);
    }

    public bool HasActiveAnnotationDraft => runtimeAnnotationStrokes.Exists(stroke =>
        stroke != null && stroke.IsDraft && stroke.NodeCount > 0);

    // Filters

}
