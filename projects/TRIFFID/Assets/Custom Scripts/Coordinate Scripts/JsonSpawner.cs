using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class JsonSpawner : MonoBehaviour
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

    [Header("Runtime Collider State")]
    [Tooltip("Initial enabled state applied to colliders on spawned annotation objects.")]
    [SerializeField] private bool startWithCollidersEnabled;

    [Header("Surface Snap Fallback")]
    [Tooltip("When altitude is missing, attempts to raycast the annotation onto a configured map surface.")]
    [SerializeField] private bool enableSurfaceSnapFallback = true;
    [Tooltip("Physics layers considered by surface-snap raycasts.")]
    [SerializeField] private LayerMask surfaceSnapMask = ~0;
    [Min(0f)]
    [Tooltip("Vertical distance above the estimated annotation position where a surface-snap ray begins.")]
    [SerializeField] private float surfaceSnapRayStartOffset = 25f;
    [Min(0f)]
    [Tooltip("Maximum distance used by each surface-snap raycast.")]
    [SerializeField] private float surfaceSnapRayDistance = 100f;
    [Tooltip("Offset applied along the hit normal after a successful surface snap.")]
    [SerializeField] private float surfaceSnapOffset = 0.01f;
    [Tooltip("Additional upward fallback offset used by the surface-snap placement flow.")]
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
    private readonly object fileWriteLock = new object();
    private readonly object dataMutationLock = new object();
    private readonly HashSet<string> reservedFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    private List<LineData> activeLines = new List<LineData>();
    private readonly Dictionary<Transform, string> lineNodeColorHexByNode = new Dictionary<Transform, string>();
    private Dictionary<string, GameObject> prefabDictionaryNormalized;
    private readonly Dictionary<string, LineData> activeLinesByObjectName = new Dictionary<string, LineData>(StringComparer.Ordinal);
    private bool spawnInProgress;
    private bool spawnQueued;
    private bool collidersDesiredActive;
    private bool latestMqttAltitudeCached;
    private double latestMqttCachedAltitude;
    private string resolvedSaveFilePath;

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

    private void Awake()
    {
        if (string.IsNullOrWhiteSpace(geoJsonPath))
            Debug.LogError("[JsonSpawner] GeoJSON input path is empty.", this);

        if (string.IsNullOrWhiteSpace(transformJsonPath))
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

    internal string GetResolvedSaveFilePath()
    {
        if (!string.IsNullOrWhiteSpace(resolvedSaveFilePath) &&
            string.Equals(saveFilePath, resolvedSaveFilePath, StringComparison.Ordinal))
        {
            return resolvedSaveFilePath;
        }

        string sanitizedPath = SanitizeSaveFilePath(saveFilePath);
        resolvedSaveFilePath = ResolveSavePathForRuntime(sanitizedPath, saveInsideProjectFolder);
        saveFilePath = resolvedSaveFilePath;
        return resolvedSaveFilePath;
    }

    private bool TryGetPrefabForClass(string className, out GameObject prefab)
    {
        prefab = null;

        if (string.IsNullOrWhiteSpace(className))
            return false;

        if (prefabDictionary != null && prefabDictionary.TryGetValue(className, out prefab) && prefab != null)
            return true;

        if (prefabDictionaryNormalized == null || prefabDictionaryNormalized.Count == 0)
            return false;

        string normalized = NormalizePrefabToken(className);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (prefabDictionaryNormalized.TryGetValue(normalized, out prefab) && prefab != null)
            return true;

        if (normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
        {
            string singular = normalized.Substring(0, normalized.Length - 1);
            if (prefabDictionaryNormalized.TryGetValue(singular, out prefab) && prefab != null)
                return true;
        }
        else
        {
            string plural = normalized + "s";
            if (prefabDictionaryNormalized.TryGetValue(plural, out prefab) && prefab != null)
                return true;
        }

        return false;
    }

    private static string NormalizePrefabToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
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

            ClearSpawnedObjects();
            transformData = loadedTransform;
            transformLoadWarningShown = false;
            currentRootData = loadedRoot;

            try
            {

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

            if (infoPanel != null && infoPanel.gameObject.activeSelf)
                infoPanel.ShowFirst();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonSpawner] Failed to parse GeoJSON file '{loadPath}': {ex.Message}", this);
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

    private bool TryPreflightSpawnInputs(out TransformConfig loadedTransform, out Root loadedRoot, out string loadPath)
    {
        loadedTransform = null;
        loadedRoot = null;
        loadPath = string.Empty;

        string resolvedTransformPath = ResolveJsonPathForRuntime(transformJsonPath);
        if (!ValidateTransformConfigFile(resolvedTransformPath, true))
            return false;

        try
        {
            loadedTransform = JsonConvert.DeserializeObject<TransformConfig>(File.ReadAllText(resolvedTransformPath));
            if (!TryValidateTransformConfig(loadedTransform, out string transformError))
            {
                Debug.LogError($"[JsonSpawner] Invalid transform config: {transformError}", this);
                loadedTransform = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to parse transform config '{resolvedTransformPath}': {ex.Message}", this);
            loadedTransform = null;
            return false;
        }

        string resolvedGeoPath = ResolveJsonPathForRuntime(geoJsonPath);
        bool configuredGeoJsonTypeValid = ValidateGeoJsonFileType(resolvedGeoPath, true);
        loadPath = resolvedGeoPath;

        try
        {
            if (!string.Equals(Path.GetFullPath(saveFilePath), Path.GetFullPath(resolvedGeoPath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(saveFilePath))
            {
                loadPath = saveFilePath;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to resolve GeoJSON snapshot path: {ex.Message}", this);
            return false;
        }

        if (string.Equals(loadPath, resolvedGeoPath, StringComparison.OrdinalIgnoreCase) && !configuredGeoJsonTypeValid)
            return false;

        if (!ValidateGeoJsonInputFile(loadPath))
            return false;

        try
        {
            loadedRoot = JsonConvert.DeserializeObject<Root>(File.ReadAllText(loadPath));
            if (loadedRoot == null)
            {
                Debug.LogError("[JsonSpawner] Invalid GeoJSON root: document is null.", this);
                return false;
            }

            if (loadedRoot.features == null)
            {
                Debug.LogError("[JsonSpawner] Invalid GeoJSON root: features collection is missing.", this);
                loadedRoot = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to parse GeoJSON file '{loadPath}': {ex.Message}", this);
            loadedRoot = null;
            return false;
        }

        return true;
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
        if (spawnedData != null)
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

    public bool SyncData(PointData data)
    {
        return SyncData(data, null);
    }

    public bool SyncData(PointData data, Transform sourceNode)
    {
        if (data == null || currentRootData?.features == null)
            return false;

        if (!IsValidCoordinateSet(data.latitude, data.longitude, data.altitude))
        {
            Debug.LogWarning($"[JsonSpawner] SyncData skipped for pointID={data.pointID}. Invalid coordinates.");
            return false;
        }

        bool updated = false;
        string targetId = data.pointID ?? string.Empty;

        foreach (var mapping in nodeMappings)
        {
            if (mapping == null || mapping.parentFeature == null)
                continue;

            if (sourceNode != null && mapping.node != sourceNode)
                continue;

            string featureId = mapping.parentFeature.id;
            if (!string.Equals(featureId, targetId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (mapping.coordArray != null)
            {
                if (mapping.coordArray.Count >= 2)
                {
                    mapping.coordArray[0] = data.longitude;
                    mapping.coordArray[1] = data.latitude;
                }

                if (mapping.coordArray.Count >= 3)
                    mapping.coordArray[2] = data.altitude;
            }

            if (mapping.parentFeature.properties != null)
                mapping.parentFeature.properties.altitude_m = (float)data.altitude;

            updated = true;
        }

        if (updated)
        {
            MarkDataDirty();
            return true;
        }

        UpdateCoordinatesChangesJson(data);
        return false;
    }

    public void SaveCurrentStateToPersistentStorage()
    {
        if (currentRootData == null)
        {
            Debug.LogWarning("[JsonSpawner] No data to save.");
            return;
        }

        if (dataVersion <= lastSavedVersion && !saveQueued)
            return;

        bool hasNoFeatures = currentRootData?.features != null && currentRootData.features.Count == 0;
        if (!hasNoFeatures && !HasValidDataForSave())
        {
            Debug.LogWarning("[JsonSpawner] Save skipped because validation failed (NaN/zero coordinates).");
            return;
        }

        if (saveInProgress)
        {
            saveQueued = true;
            return;
        }

        SyncWorldToJSON();
        NormalizeForJsonWrite();
        currentRootData.lastModified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        string json;
        try
        {
            json = JsonConvert.SerializeObject(currentRootData, Formatting.Indented);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to serialize JSON: {ex.Message}");
            return;
        }

        int saveVersion = dataVersion;
        _ = WriteJsonAsync(json, saveVersion);
    }

    internal int LocalPersistenceVersion => dataVersion;

    internal bool HasPendingLocalPersistence =>
        saveInProgress || saveQueued || dataVersion > lastSavedVersion;

    internal bool TryWriteExternalJsonSnapshotAtomically(string json, int expectedLocalVersion)
    {
        if (HasPendingLocalPersistence || dataVersion != expectedLocalVersion)
            return false;

        string targetPath = GetResolvedSaveFilePath();

        lock (fileWriteLock)
        {
            if (HasPendingLocalPersistence || dataVersion != expectedLocalVersion)
                return false;

            AtomicWriteAllText(targetPath, json);
        }

        return true;
    }

    public void UpdateIconPositionInJson(FloatingIcon movedIcon)
    {
        NodeMapping mapping = nodeMappings.Find(m => m.node == movedIcon.transform);
        PointData movedData = movedIcon.GetComponent<PointData>();
        if (movedData != null)
            MarkerEventManager.RaiseMarkerMoved(movedData);
        
        if (mapping != null)
        {
            if (!mapping.isCentroid && mapping.parentCentroid != null)
                RecalculateCentroidForNode(mapping);

            SyncData(movedData);
            SaveCurrentStateToPersistentStorage();
            Debug.Log($"[JsonSpawner] Position saved for mapped object.");
        }
        else
        {
            PointData pd = movedIcon.GetComponent<PointData>();
            if (pd != null)
                UpdateCoordinatesChangesJson(pd);
        }
    }

    private async Task WriteJsonAsync(string json, int saveVersion)
    {
        saveInProgress = true;

        string localSavePath = saveFilePath;

        try
        {
            await Task.Run(() =>
            {
                lock (fileWriteLock)
                {
                    AtomicWriteAllText(localSavePath, json);
                }
            });

            lastSavedVersion = Math.Max(lastSavedVersion, saveVersion);
            autoSaveTimer = 0f;
            Debug.Log($"[JsonSpawner] Saved to: {saveFilePath} (lastModified: {currentRootData.lastModified})");

        #if UNITY_EDITOR
                    UnityEditor.AssetDatabase.Refresh();
        #endif
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Async save failed: {ex.Message}");
        }
        finally
        {
            saveInProgress = false;
            if (saveQueued || dataVersion > lastSavedVersion)
            {
                saveQueued = false;
                SaveCurrentStateToPersistentStorage();
            }
        }
    }

    private void FlushPendingSaveSynchronously()
    {
        if (currentRootData == null)
            return;

        if (dataVersion <= lastSavedVersion && !saveInProgress && !saveQueued)
            return;

        if (!HasValidDataForSave())
            return;

        SyncWorldToJSON();
        NormalizeForJsonWrite();
        currentRootData.lastModified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        string json;
        try
        {
            json = JsonConvert.SerializeObject(currentRootData, Formatting.Indented);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Sync flush serialize failed: {ex.Message}");
            return;
        }

        try
        {
            lock (fileWriteLock)
            {
                AtomicWriteAllText(saveFilePath, json);
            }

            lastSavedVersion = dataVersion;
            autoSaveTimer = 0f;
            saveQueued = false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Sync flush write failed: {ex.Message}");
        }
    }

    private static void AtomicWriteAllText(string targetPath, string content)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("targetPath is null or empty", nameof(targetPath));

        string targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        string tempPath = targetPath + ".tmp";

        try
        {
            using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(targetPath))
            {
                EnsureFileWritable(targetPath);

                try
                {
                    File.Replace(tempPath, targetPath, null);
                }
                catch (IOException)
                {
                    WriteDirectOverwrite(targetPath, content);
                }
                catch (UnauthorizedAccessException)
                {
                    EnsureFileWritable(targetPath);
                    WriteDirectOverwrite(targetPath, content);
                }
            }
            else
                File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WriteDirectOverwrite(string targetPath, string content)
    {
        using (FileStream stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (StreamWriter writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(true);
        }
    }

    private static void EnsureFileWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        FileAttributes attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }

    private void MarkDataDirty()
    {
        dataVersion++;
    }

    private bool HasValidDataForSave()
    {
        if (currentRootData?.features == null || currentRootData.features.Count == 0)
            return false;

        foreach (var feature in currentRootData.features)
        {
            if (feature?.geometry?.coordinates == null)
                continue;

            if (!TryExtractFirstCoordinateTriplet(feature.geometry.coordinates, out double lon, out double lat, out double altFromCoords))
                continue;

            double alt = feature.properties?.altitude_m ?? 0f;
            if (Math.Abs(alt) < 1e-9)
                alt = altFromCoords;

            if (IsValidCoordinateSet(lat, lon, alt))
                return true;
        }

        return false;
    }

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

    private void NormalizeForJsonWrite()
    {
        if (currentRootData?.features == null)
            return;

        foreach (Feature feature in currentRootData.features)
        {
            if (feature == null)
                continue;

            feature.id = NormalizeLower(feature.id);

            if (feature.properties == null)
                continue;

            feature.properties.className = NormalizeLower(feature.properties.className);
            feature.properties.id = NormalizeLower(feature.properties.id);
            feature.properties.category = NormalizeLower(feature.properties.category);
            feature.properties.source = NormalizeLower(feature.properties.source);
            feature.properties.marker_color = NormalizeLower(feature.properties.marker_color);
            feature.properties.fill = NormalizeLower(feature.properties.fill);

            EnsurePolygonFillStyle(feature);
        }
    }

    private static void EnsurePolygonFillStyle(Feature feature)
    {
        if (feature?.properties == null)
            return;

        bool isPolygon = string.Equals(feature.geometry?.type, "Polygon", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(feature.geometry?.type, "MultiPolygon", StringComparison.OrdinalIgnoreCase);
        if (!isPolygon)
            return;

        string markerHex = feature.properties.marker_color;
        if (!string.IsNullOrWhiteSpace(markerHex) && ColorUtility.TryParseHtmlString(markerHex, out _))
            feature.properties.fill = markerHex.StartsWith("#", StringComparison.Ordinal) ? markerHex.ToLowerInvariant() : ("#" + markerHex.ToLowerInvariant());

        feature.properties.fill_opacity = 0.25f;
    }

    private static string NormalizeLower(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Trim().ToLowerInvariant();
    }

    private static string ResolveCoordinatesChangesPath()
    {
        string assetPath = Path.Combine(Application.dataPath, "Data", "coordinates_changes.json");

        if (Application.isPlaying)
        {
            string runtimeDir = Path.Combine(Application.persistentDataPath, "Data");
            if (!Directory.Exists(runtimeDir))
                Directory.CreateDirectory(runtimeDir);

            string runtimePath = Path.Combine(runtimeDir, "coordinates_changes.json");
            if (!File.Exists(runtimePath) && File.Exists(assetPath))
                File.Copy(assetPath, runtimePath, false);

            return runtimePath;
        }

        return assetPath;
    }

    private void UpdateCoordinatesChangesJson(PointData pd)
    {
        string changesPath = ResolveCoordinatesChangesPath();
        if (!File.Exists(changesPath)) return;
        
        try
        {
            var root = JObject.Parse(File.ReadAllText(changesPath));
            var features = root["features"] as JArray;
            if (features != null)
            {
                foreach (var f in features)
                {
                    JObject props = f["properties"] as JObject;
                    if (props != null)
                    {
                        props["class"] = NormalizeLower(props["class"]?.ToString());
                        props["id"] = NormalizeLower(props["id"]?.ToString());
                        props["category"] = NormalizeLower(props["category"]?.ToString());
                        props["source"] = NormalizeLower(props["source"]?.ToString());
                        props["marker-color"] = NormalizeLower(props["marker-color"]?.ToString());
                    }

                    string fId = f["id"]?.ToString();
                    if (fId == pd.pointID)
                    {
                        f["id"] = NormalizeLower(f["id"]?.ToString());
                        f["geometry"]["coordinates"] = new JArray { pd.longitude, pd.latitude, pd.altitude };
                        f["properties"]["altitude_m"] = (float)pd.altitude;
                        break;
                    }
                }
                File.WriteAllText(changesPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
                Debug.Log($"[JsonSpawner] Live Move Saved for New Object ID: {pd.pointID}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to update coordinates_changes.json on move: {ex.Message}");
        }
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

        foreach (var mapping in nodeMappings)
        {
            if (mapping.node == null) continue;

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

            if (mapping.parentFeature.properties != null)
                mapping.parentFeature.properties.altitude_m = (float)wgs84.alt;

            SyncPointDataFromWgs(mapping.node, wgs84);
        }

        EnforceClosedPolygonRings();
    }

    private static Vector3 GetStableMapLocalPosition(Transform node, Transform defaultMapTransform)
    {
        if (node == null)
            return Vector3.zero;

        FloatingIcon icon = node.GetComponent<FloatingIcon>();
        if (icon != null && !icon.IsBeingManipulated)
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
        if (currentRootData?.features == null)
            return;

        foreach (Feature feature in currentRootData.features)
        {
            if (feature?.geometry?.coordinates == null)
                continue;

            string geometryType = feature.geometry.type;
            if (string.Equals(geometryType, "Polygon", StringComparison.OrdinalIgnoreCase))
            {
                JArray polygon = feature.geometry.coordinates as JArray;
                if (polygon == null)
                    continue;

                foreach (JArray ring in polygon)
                    EnsureRingClosed(ring);
            }
            else if (string.Equals(geometryType, "MultiPolygon", StringComparison.OrdinalIgnoreCase))
            {
                JArray multiPolygon = feature.geometry.coordinates as JArray;
                if (multiPolygon == null)
                    continue;

                foreach (JArray polygon in multiPolygon)
                {
                    if (polygon == null)
                        continue;

                    foreach (JArray ring in polygon)
                        EnsureRingClosed(ring);
                }
            }
        }
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
            ring[ring.Count - 1] = closing;
        else
            ring.Add(closing);
    }

    private void SetupIcon(GameObject obj)
    {
        FloatingIcon icon = obj.GetComponent<FloatingIcon>();
        if (icon != null) 
        { 
            Transform parentToUse = mapTransform != null ? mapTransform : transform;
            icon.Setup(parentToUse, this); 
        }
    }

    private void RegisterToInfoPanel(GameObject obj)
    {
        if (infoPanel == null) return;

        PointData data = obj.GetComponent<PointData>();
        PulseEffect pulse = obj.GetComponent<PulseEffect>();
        if (pulse == null)
            pulse = obj.GetComponentInChildren<PulseEffect>(true);

        if (data != null)
        {
            infoPanel.RegisterMarker(data, pulse);

            MarkerInteractionRelay relay = obj.GetComponent<MarkerInteractionRelay>();
            if (relay == null) relay = obj.AddComponent<MarkerInteractionRelay>();
            EnsureHoverInteractable(obj);
            relay.Setup(infoPanel);
        }
    }

    private static void EnsureHoverInteractable(GameObject obj)
    {
        if (obj == null)
            return;

        if (obj.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>() != null)
            return;

        obj.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>();
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

            if (string.Equals(data.pointID, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(data.pointClass, LatestMqttClassName, StringComparison.OrdinalIgnoreCase))
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

        if (!useCachedAltitude && enableSurfaceSnapFallback)
        {
            Vector3 worldPosition = parentRef.TransformPoint(targetPosition);
            if (TrySnapToSurface(worldPosition, parentRef, out Vector3 snappedWorldPos))
            {
                markerObject.transform.position = snappedWorldPos;
                Vector3 snappedLocalPos = parentRef.InverseTransformPoint(snappedWorldPos);
                JsonSpawner.Vector3Double snappedWgs = ColmapToWgs84(snappedLocalPos);
                altitude = snappedWgs.alt;
                targetPosition = parentRef.InverseTransformPoint(snappedWorldPos);
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
        markerData.confidence = 1f;
        markerData.pointClass = LatestMqttClassName;
        markerData.category = "telemetry";
        markerData.source = string.IsNullOrWhiteSpace(response.topic) ? "mqtt" : response.topic;
        markerData.pointID = LatestMqttFeatureId;
        markerData.behavior = string.IsNullOrWhiteSpace(response.message.behavior) ? "STOPPED" : response.message.behavior;

        latestMqttCachedAltitude = altitude;
        latestMqttAltitudeCached = true;

        markerData.NotifyDataChanged();
        MarkerEventManager.RaiseMarkerMoved(markerData);
    }

    private static bool TryValidateFeatureForSpawn(Feature feature, out string error)
    {
        if (feature == null)
        {
            error = "feature is null";
            return false;
        }

        return TryValidateGeometryForSpawn(feature.geometry, out error);
    }

    private static bool TryValidateGeometryForSpawn(Geometry geometry, out string error)
    {
        if (geometry == null)
        {
            error = "geometry is null";
            return false;
        }

        if (geometry.type == "GeometryCollection")
        {
            if (geometry.geometries != null)
            {
                for (int i = 0; i < geometry.geometries.Count; i++)
                {
                    if (!TryValidateGeometryForSpawn(geometry.geometries[i], out string nestedError))
                    {
                        error = $"geometry collection item {i}: {nestedError}";
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }

        int coordinateNestingDepth;
        switch (geometry.type)
        {
            case "Point":
                coordinateNestingDepth = 0;
                break;
            case "MultiPoint":
            case "LineString":
                coordinateNestingDepth = 1;
                break;
            case "MultiLineString":
            case "Polygon":
                coordinateNestingDepth = 2;
                break;
            case "MultiPolygon":
                coordinateNestingDepth = 3;
                break;
            default:
                error = string.Empty;
                return true;
        }

        return TryValidateCoordinateStructure(geometry.coordinates, coordinateNestingDepth, out error);
    }

    private static bool TryValidateCoordinateStructure(JToken token, int nestingDepth, out string error)
    {
        if (!(token is JArray array))
        {
            error = "coordinates are not an array";
            return false;
        }

        if (nestingDepth > 0)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (!TryValidateCoordinateStructure(array[i], nestingDepth - 1, out string nestedError))
                {
                    error = $"coordinate item {i}: {nestedError}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        if (array.Count < 2)
        {
            error = "coordinate contains fewer than two values";
            return false;
        }

        if (!TryGetDouble(array[0], out double lon) || !TryGetDouble(array[1], out double lat) ||
            !IsFiniteNumber(lon) || !IsFiniteNumber(lat))
        {
            error = "longitude or latitude is missing, non-numeric, or non-finite";
            return false;
        }

        if (array.Count >= 3 && array[2] != null && array[2].Type != JTokenType.Null &&
            (!TryGetDouble(array[2], out double altitude) || !IsFiniteNumber(altitude)))
        {
            error = "altitude is non-numeric or non-finite";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFiniteNumber(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private void ProcessGeometry(Geometry geom, Feature feature, Color color)
    {
        if (geom == null) return;

        if (geom.type == "GeometryCollection")
        {
            if (geom.geometries != null)
                foreach (var subGeom in geom.geometries)
                    ProcessGeometry(subGeom, feature, color);
            return;
        }

        if (geom.coordinates == null) return;
        JArray coords = geom.coordinates as JArray;
        if (coords == null) return;

        switch (geom.type)
        {
            case "Point": 
                SpawnPointNode(coords[0].Value<double>(), coords[1].Value<double>(), coords, feature, color, false, null, false); 
                break;
            case "MultiPoint": 
                foreach (JArray pt in coords) SpawnPointNode(pt[0].Value<double>(), pt[1].Value<double>(), pt, feature, color, false, null, false); 
                break;
            case "LineString": 
                SpawnLinePath(coords, feature, color, null); 
                break;
            case "MultiLineString":
                foreach (JArray line in coords) SpawnLinePath(line, feature, color, null); 
                break;
            case "Polygon": 
                Transform polyCentroid = null;
                if (coords.Count > 0 && coords[0] is JArray extRing && extRing.Count > 0)
                    polyCentroid = SpawnCentroidNode(extRing, coords, feature, color);
                foreach (JArray ring in coords) SpawnLinePath(ring, feature, color, polyCentroid); 
                break;
            case "MultiPolygon": 
                foreach (JArray polygon in coords) {
                    Transform multiPolyCentroid = null;
                    if (polygon.Count > 0 && polygon[0] is JArray pRing && pRing.Count > 0)
                        multiPolyCentroid = SpawnCentroidNode(pRing, polygon, feature, color);
                    foreach (JArray ring in polygon) SpawnLinePath(ring, feature, color, multiPolyCentroid); 
                }
                break;
        }
    }

    private Transform SpawnCentroidNode(JArray ring, JArray originalToken, Feature feature, Color color)
    {
        double sumLon = 0, sumLat = 0;
        int count = 0;

        int limit = ring.Count;
        if (ring.Count > 1 && ring[0] is JArray firstRingPt && ring[ring.Count - 1] is JArray lastRingPt &&
            firstRingPt[0].Value<double>() == lastRingPt[0].Value<double>() &&
            firstRingPt[1].Value<double>() == lastRingPt[1].Value<double>())
            limit = ring.Count - 1;

        for (int i = 0; i < limit; i++)
        {
            JArray pt = ring[i] as JArray;
            if (pt == null) continue;
            sumLon += pt[0].Value<double>();
            sumLat += pt[1].Value<double>();
            count++;
        }

        if (count > 0)
            return SpawnPointNode(sumLon / count, sumLat / count, originalToken, feature, color, true, null, false);
        return null;
    }

    private Transform SpawnPointNode(double lon, double lat, JArray token, Feature feature, Color fallbackColor, bool isCentroid, Transform customParent, bool isVertex, double? fallbackAltitude = null)
    {
        string className = feature.properties?.className ?? "Unknown";
        string id     = feature.id ?? feature.properties?.id ?? "-";
        string geometryType = feature.geometry?.type;
        bool isLineNode = isVertex || string.Equals(geometryType, "LineString", StringComparison.OrdinalIgnoreCase) || string.Equals(geometryType, "MultiLineString", StringComparison.OrdinalIgnoreCase);
        bool isPolygonNode = isVertex && (string.Equals(geometryType, "Polygon", StringComparison.OrdinalIgnoreCase) || string.Equals(geometryType, "MultiPolygon", StringComparison.OrdinalIgnoreCase));

        bool hasAltitudeFromToken = TryGetAltitudeFromCoordinateToken(token, out double coordAlt);
        bool hasAltitudeFromProperties = feature.properties?.altitude_m.HasValue ?? false;

        double alt = hasAltitudeFromProperties ? feature.properties.altitude_m.Value : fallbackAltitude ?? 0.0;
        if (hasAltitudeFromToken)
            alt = coordAlt;

        bool shouldFallbackToSurface = enableSurfaceSnapFallback && !hasAltitudeFromToken && !hasAltitudeFromProperties;

        Vector3 pos   = GetColmapPosition(lon, lat, alt);

        GameObject prefabToUse = nodePrefab;

        if (!isVertex)
        {
            if (TryGetPrefabForClass(className, out GameObject pref) && pref != null)
                prefabToUse = pref;
            else if (!isLineNode)
                return null;
        }

        if (prefabToUse != null)
        {
            Transform parentRef = mapTransform != null ? mapTransform : transform;

            GameObject obj = Instantiate(prefabToUse);
            obj.transform.SetParent(parentRef, false);
            obj.transform.localScale = prefabToUse.transform.localScale;
            EnsurePositiveScaleForBoxCollider(obj);
            obj.transform.position = parentRef.TransformPoint(pos);

            if (shouldFallbackToSurface && TrySnapToSurface(obj.transform.position, parentRef, out Vector3 snappedWorldPos))
            {
                obj.transform.position = snappedWorldPos;

                Vector3 snappedLocalPos = parentRef.InverseTransformPoint(snappedWorldPos);
                Vector3Double snappedWgs = ColmapToWgs84(snappedLocalPos);
                alt = snappedWgs.alt;

                TryWriteAltitudeToCoordinateToken(token, alt);
                if (feature.properties != null)
                    feature.properties.altitude_m = (float)alt;
            }
            else if (shouldFallbackToSurface)
            {
                Debug.LogWarning($"[JsonSpawner] Surface snap failed for {className}_{id} (missing/null altitude). Keeping fallback altitude={alt:F3}.");
            }
            
            if (isCentroid)    obj.name = $"Centroid_{className}_{id}";
            else if (isVertex) obj.name = $"Corner_{className}_{id}";
            else               obj.name = $"{className}_{id}";

            obj.tag = "MapMarker";

            PointData data = obj.GetComponent<PointData>();
            if (data == null) data = obj.AddComponent<PointData>();
            
            data.latitude   = lat;
            data.longitude  = lon;
            data.altitude   = alt;
            data.confidence = feature.properties?.confidence ?? 0f;
            if (isLineNode)
            {
                string lineColorHex = ResolveLineColorHexFromFeatureOrCache(feature, obj.transform, fallbackColor);
                data.lineColorHex = lineColorHex;
                data.pointClass = feature.properties?.className ?? "road";
                data.category = feature.properties?.category ?? "navigation";
                CacheLineNodeColorHex(obj.transform, lineColorHex);
            }
            else
            {
                data.lineColorHex = string.Empty;
                data.pointClass = className;
                data.category = feature.properties?.category ?? "-";
            }
            data.pointID    = id; 
            data.source     = feature.properties?.source ?? "-";

            if (prefabToUse == nodePrefab) ApplyColor(obj, fallbackColor);

            spawnedObjects.Add(obj);
            nodeMappings.Add(new NodeMapping { 
                node           = obj.transform, 
                coordArray     = token, 
                parentFeature  = feature,
                isCentroid     = isCentroid,
                parentCentroid = customParent 
            });
            
            SetupIcon(obj);
            RegisterToInfoPanel(obj);

            return obj.transform;
        }
        return null;
    }

    private static bool TryGetAltitudeFromCoordinateToken(JArray token, out double altitude)
    {
        altitude = 0d;
        if (token == null || token.Count < 3)
            return false;

        if (!TryGetDouble(token[0], out _) || !TryGetDouble(token[1], out _))
            return false;

        return TryGetDouble(token[2], out altitude);
    }

    private static bool TryWriteAltitudeToCoordinateToken(JArray token, double altitude)
    {
        if (token == null || token.Count < 2)
            return false;

        if (!TryGetDouble(token[0], out _) || !TryGetDouble(token[1], out _))
            return false;

        if (token.Count >= 3)
            token[2] = altitude;
        else
            token.Add(altitude);

        return true;
    }

    private bool TrySnapToSurface(Vector3 approximateWorldPos, Transform parentRef, out Vector3 snappedWorldPos)
    {
        snappedWorldPos = approximateWorldPos;

        if (parentRef == null)
            return false;

        bool activatedSurfaceForSnap = false;
        PrepareSurfaceForSnap(ref activatedSurfaceForSnap);

        try
        {
            Vector3 castAxis = parentRef.up.sqrMagnitude > 1e-6f ? parentRef.up.normalized : Vector3.up;
            float rayStartOffset = Mathf.Max(0.25f, surfaceSnapRayStartOffset);
            float rayDistance = Mathf.Max(0.5f, surfaceSnapRayDistance);
            float snapLift = Mathf.Max(0f, surfaceSnapOffset + surfaceSnapLift);

            if (TryRaycastSurfaceFromPivot(approximateWorldPos, castAxis, rayStartOffset, rayDistance, snapLift, out snappedWorldPos))
                return true;

            float extendedStartOffset = Mathf.Max(rayStartOffset * 8f, 250f);
            float extendedDistance = Mathf.Max(rayDistance * 16f, 2000f);
            if (TryRaycastSurfaceFromPivot(approximateWorldPos, castAxis, extendedStartOffset, extendedDistance, snapLift, out snappedWorldPos))
                return true;

            if (surfaceSnapTargetObject != null)
            {
                Collider targetCollider = surfaceSnapTargetObject.GetComponentInChildren<Collider>(true);
                if (targetCollider != null)
                {
                    Vector3 boundsCenter = targetCollider.bounds.center;
                    float boundsExtent = Mathf.Max(1f, targetCollider.bounds.extents.magnitude);

                    if (TryRaycastSurfaceFromPivot(boundsCenter, castAxis, boundsExtent + 5f, boundsExtent * 2f + 20f, snapLift, out snappedWorldPos))
                        return true;
                }
            }

            return false;
        }
        finally
        {
            if (activatedSurfaceForSnap && restoreSurfaceActiveStateAfterSnap && surfaceSnapTargetObject != null)
                surfaceSnapTargetObject.SetActive(false);
        }
    }

    private bool TryRaycastSurfaceFromPivot(Vector3 pivot, Vector3 castAxis, float rayStartOffset, float rayDistance, float snapLift, out Vector3 snappedWorldPos)
    {
        snappedWorldPos = pivot;

        Vector3 rayStartFromAbove = pivot + castAxis * Mathf.Max(0.25f, rayStartOffset);
        if (Physics.Raycast(rayStartFromAbove, -castAxis, out RaycastHit hitDown, Mathf.Max(0.5f, rayDistance), surfaceSnapMask, QueryTriggerInteraction.Ignore))
        {
            snappedWorldPos = hitDown.point + castAxis * snapLift;
            return true;
        }

        Vector3 rayStartFromBelow = pivot - castAxis * Mathf.Max(0.25f, rayStartOffset);
        if (Physics.Raycast(rayStartFromBelow, castAxis, out RaycastHit hitUp, Mathf.Max(0.5f, rayDistance), surfaceSnapMask, QueryTriggerInteraction.Ignore))
        {
            snappedWorldPos = hitUp.point + castAxis * snapLift;
            return true;
        }

        return false;
    }

    private void PrepareSurfaceForSnap(ref bool activatedSurfaceForSnap)
    {
        if (surfaceSnapTargetObject != null && autoActivateSurfaceForSnap && !surfaceSnapTargetObject.activeSelf)
        {
            surfaceSnapTargetObject.SetActive(true);
            activatedSurfaceForSnap = true;
        }

        if (surfaceSnapSyncBehaviour != null)
            surfaceSnapSyncBehaviour.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);
        else if (surfaceSnapTargetObject != null)
            surfaceSnapTargetObject.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);
    }

    private void SpawnLinePath(JArray lineToken, Feature feature, Color color, Transform customParent)
    {
        List<Transform> nodes = new List<Transform>();
        string resolvedLineColorHex = ResolveLineColorHex(feature, color);
        Color resolvedLineColor = color;
        if (!string.IsNullOrEmpty(resolvedLineColorHex) && ColorUtility.TryParseHtmlString(resolvedLineColorHex, out Color parsedLineColor))
            resolvedLineColor = parsedLineColor;

        bool forceClosedLoop = string.Equals(feature.geometry?.type, "Polygon", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(feature.geometry?.type, "MultiPolygon", StringComparison.OrdinalIgnoreCase);
        bool hasExplicitClosingPoint = false;
        if (lineToken.Count > 2)
        {
            JArray firstPt = lineToken[0] as JArray;
            JArray lastPt  = lineToken[lineToken.Count - 1] as JArray;
            if (firstPt != null && lastPt != null && AreCoordinatePairsNear(firstPt, lastPt))
                hasExplicitClosingPoint = true;
        }
        bool isClosedLoop = forceClosedLoop || hasExplicitClosingPoint;

        for (int i = 0; i < lineToken.Count; i++)
        {
            JArray ptToken = lineToken[i] as JArray;
            if (ptToken == null) continue;

            if (hasExplicitClosingPoint && i == lineToken.Count - 1 && nodes.Count > 0)
            {
                Transform firstNode = nodes[0];
                nodes.Add(firstNode); 
                nodeMappings.Add(new NodeMapping { 
                    node           = firstNode, 
                    coordArray     = ptToken, 
                    parentFeature  = feature, 
                    isCentroid     = false,
                    parentCentroid = customParent 
                });
                continue;
            }

            double lon = ptToken[0].Value<double>(); 
            double lat = ptToken[1].Value<double>();
            
            Transform nodeTransform = SpawnPointNode(lon, lat, ptToken, feature, resolvedLineColor, false, customParent, true);
            if (nodeTransform != null)
                nodes.Add(nodeTransform);
        }

        if (nodes.Count > 1)
        {
            Transform parentRef   = mapTransform != null ? mapTransform : transform;
            Transform parentToUse = customParent != null ? customParent : parentRef;

            string lineName    = feature.properties?.className ?? "Path";
            GameObject lineObj = new GameObject($"Line_{lineName}_{feature.id}");
            lineObj.transform.SetParent(parentToUse, false);

            LineRenderer lr   = lineObj.AddComponent<LineRenderer>();
            lr.useWorldSpace  = true;
            lr.startWidth     = lr.endWidth = lineThickness;
            lr.positionCount  = nodes.Count;
            lr.loop           = isClosedLoop;

            Shader fallbackShader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            Material mat = lineMaterial != null ? new Material(lineMaterial) : new Material(fallbackShader != null ? fallbackShader : Shader.Find("Hidden/InternalErrorShader"));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", resolvedLineColor);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", resolvedLineColor);
            
            lr.sharedMaterial = mat; 
            lr.startColor     = lr.endColor = resolvedLineColor;

            RuntimeLine runtimeLine = lineObj.AddComponent<RuntimeLine>();
            runtimeLine.Initialize(lr);
            foreach (Transform node in nodes)
            {
                runtimeLine.AddNode(node);
                if (node == null) continue;

                PointData pointData = node.GetComponent<PointData>();
                if (pointData != null)
                {
                    pointData.lineColorHex = resolvedLineColorHex;
                    pointData.pointClass = "Drawing";
                    pointData.category = "Annotation";
                }

                CacheLineNodeColorHex(node, resolvedLineColorHex);
            }

            activeLines.Add(new LineData { renderer = lr, nodes = nodes, createdMaterial = mat, parentFeature = feature });
            RegisterActiveLineByName(lineObj.name, activeLines[activeLines.Count - 1]);
            spawnedObjects.Add(lineObj);
        }
    }

    private static bool AreCoordinatePairsNear(JArray a, JArray b)
    {
        if (a == null || b == null || a.Count < 2 || b.Count < 2)
            return false;

        const double eps = 1e-9;
        return Math.Abs(a[0].Value<double>() - b[0].Value<double>()) <= eps &&
               Math.Abs(a[1].Value<double>() - b[1].Value<double>()) <= eps;
    }

    private void RegisterActiveLineByName(string lineName, LineData lineData)
    {
        if (string.IsNullOrEmpty(lineName) || lineData == null)
            return;

        activeLinesByObjectName[lineName] = lineData;
    }

    private void UnregisterActiveLineByName(string lineName)
    {
        if (string.IsNullOrEmpty(lineName))
            return;

        activeLinesByObjectName.Remove(lineName);
    }

    public struct Vector3Double { public double lon, lat, alt, x, y, z; }

    private Vector3 GetColmapPosition(double lon, double lat, double alt)
    {
        EnsureTransformDataLoaded();
        return GeoUtils.GetColmapPosition(lon, lat, alt, transformData);
    }

    public Vector3Double ColmapToWgs84(Vector3 colmapPos)
    {
        EnsureTransformDataLoaded();
        if (transformData == null || transformData.colmap_to_enu == null || transformData.origin_wgs84 == null)
        {
            if (!transformLoadWarningShown)
            {
                Debug.LogWarning("[JsonSpawner] ColmapToWgs84: transformData not loaded yet.");
                transformLoadWarningShown = true;
            }
            return new Vector3Double();
        }

        GeoUtils.ColmapToWgs84(colmapPos, transformData, out double lon, out double lat, out double alt);
        return new Vector3Double { lon = lon, lat = lat, alt = alt };
    }

    private void EnsureTransformDataLoaded()
    {
        if (transformData != null && transformData.colmap_to_enu != null && transformData.origin_wgs84 != null)
            return;

        string resolvedPath = ResolveJsonPathForRuntime(transformJsonPath);
        bool shouldLogValidation = !transformLoadWarningShown;
        if (!ValidateTransformConfigFile(resolvedPath, shouldLogValidation))
        {
            transformLoadWarningShown = true;
            return;
        }

        try
        {
            TransformConfig loadedTransform = JsonConvert.DeserializeObject<TransformConfig>(File.ReadAllText(resolvedPath));
            if (!TryValidateTransformConfig(loadedTransform, out string transformError))
            {
                if (!transformLoadWarningShown)
                    Debug.LogError($"[JsonSpawner] Invalid transform config: {transformError}", this);

                transformLoadWarningShown = true;
                return;
            }

            transformData = loadedTransform;
            transformLoadWarningShown = false;
        }
        catch (Exception ex)
        {
            if (!transformLoadWarningShown)
            {
                Debug.LogWarning($"[JsonSpawner] Failed loading transform config: {ex.Message}");
                transformLoadWarningShown = true;
            }
        }
    }

    private double GetTransformOriginAltitude()
    {
        EnsureTransformDataLoaded();

        if (transformData?.origin_wgs84 == null)
            return 0d;

        return transformData.origin_wgs84.alt;
    }

    private static string ResolveJsonPathForRuntime(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string normalized = rawPath.Replace("/", Path.DirectorySeparatorChar.ToString());

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
            return normalized;

        return Path.Combine(projectRoot, normalized);
    }

    private static string ResolveSavePathForRuntime(string rawPath, bool useProjectFolder)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            rawPath = DefaultRelativeSavePath;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string normalized = rawPath.Replace("\\", "/").TrimStart('/');
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring("Assets/".Length);

        string basePath = Application.persistentDataPath;
        if (useProjectFolder)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(projectRoot))
                basePath = projectRoot;
        }

        string combined = Path.Combine(basePath, normalized.Replace("/", Path.DirectorySeparatorChar.ToString()));
        return combined;
    }

    private static void ValidateSavePathConfiguration(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogError("[JsonSpawner] GeoJSON save path is empty.");
            return;
        }

        if (Directory.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] GeoJSON save path points to a directory: {path}");
            return;
        }

        ValidateGeoJsonFileType(path, true, "output GeoJSON");
    }

    private static bool ValidateGeoJsonInputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogError("[JsonSpawner] GeoJSON input path is empty.");
            return false;
        }

        if (Directory.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] GeoJSON input path points to a directory: {path}");
            return false;
        }

        if (!ValidateGeoJsonFileType(path, true))
            return false;

        if (!File.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] File not found: {path}");
            return false;
        }

        return true;
    }

    private static bool ValidateTransformConfigFile(string path, bool logErrors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (logErrors)
                Debug.LogError("[JsonSpawner] Transform config path is empty.");
            return false;
        }

        if (Directory.Exists(path))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] Transform config path points to a directory: {path}");
            return false;
        }

        string extension = GetPathExtension(path);
        if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] Invalid transform config file type. Expected .json, got {FormatExtension(extension)}: {path}");
            return false;
        }

        if (!File.Exists(path))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] File not found: {path}");
            return false;
        }

        return true;
    }

    private static bool ValidateGeoJsonFileType(string path, bool logErrors, string description = "GeoJSON")
    {
        string extension = GetPathExtension(path);
        bool valid = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(extension, ".geojson", StringComparison.OrdinalIgnoreCase);
        if (!valid && logErrors)
            Debug.LogError($"[JsonSpawner] Invalid {description} file type. Expected .json or .geojson, got {FormatExtension(extension)}: {path}");

        return valid;
    }

    private static string GetPathExtension(string path)
    {
        try
        {
            return Path.GetExtension(path) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string FormatExtension(string extension)
    {
        return string.IsNullOrEmpty(extension) ? "<none>" : extension;
    }

    private static bool TryValidateTransformConfig(TransformConfig config, out string error)
    {
        if (config == null)
        {
            error = "document is null.";
            return false;
        }

        if (config.origin_wgs84 == null)
        {
            error = "origin_wgs84 is missing.";
            return false;
        }

        if (!IsFiniteNumber(config.origin_wgs84.lat) ||
            !IsFiniteNumber(config.origin_wgs84.lon) ||
            !IsFiniteNumber(config.origin_wgs84.alt))
        {
            error = "origin_wgs84 latitude, longitude, and altitude must be finite.";
            return false;
        }

        if (config.colmap_to_enu == null)
        {
            error = "colmap_to_enu is missing.";
            return false;
        }

        ColmapToEnu transform = config.colmap_to_enu;
        if (!IsFiniteNumber(transform.scale) || transform.scale == 0f)
        {
            error = "scale must be finite and non-zero.";
            return false;
        }

        if (transform.R_rowmajor == null || transform.R_rowmajor.Length < 9)
        {
            error = "R_rowmajor must contain at least 9 finite values.";
            return false;
        }

        for (int i = 0; i < 9; i++)
        {
            if (!IsFiniteNumber(transform.R_rowmajor[i]))
            {
                error = "R_rowmajor must contain at least 9 finite values.";
                return false;
            }
        }

        if (transform.t == null || transform.t.Length < 3)
        {
            error = "t must contain at least 3 finite values.";
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (!IsFiniteNumber(transform.t[i]))
            {
                error = "t must contain at least 3 finite values.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string SanitizeSaveFilePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return DefaultRelativeSavePath;

        string normalized = rawPath.Replace("\\", "/").Trim();

        if (normalized.EndsWith(LegacyCoordinatesChangesSuffix, StringComparison.OrdinalIgnoreCase))
            return DefaultRelativeSavePath;

        return normalized;
    }

    private static string SanitizeGeoJsonPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return DefaultGeoJsonPath;

        string normalized = rawPath.Replace("\\", "/").Trim();

        if (normalized.EndsWith(LegacyCoordinatesChangesSuffix, StringComparison.OrdinalIgnoreCase))
            return DefaultGeoJsonPath;

        if (normalized.StartsWith("Coordinates/", StringComparison.OrdinalIgnoreCase))
            return "Assets/" + normalized;

        return normalized;
    }

    private void OnValidate()
    {
        geoJsonPath = SanitizeGeoJsonPath(geoJsonPath);
        saveFilePath = SanitizeSaveFilePath(saveFilePath);
    }

    private void ApplyColor(GameObject obj, Color color)
    {
        Renderer[] rends = obj.GetComponentsInChildren<Renderer>();
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        foreach (var r in rends)
        {
            mpb.Clear();
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", color);
            r.SetPropertyBlock(mpb);
        }
    }

    [ContextMenu("Enable All Colliders")]
    public void EnableColliders() => SetAllColliders(true);

    [ContextMenu("Disable All Colliders")]
    public void DisableColliders() => SetAllColliders(false);

    public void SetAllColliders(bool isActive)
    {
        collidersDesiredActive = isActive;
        ApplyColliderStateToSpawnedObjects(isActive, true);
    }

    private void ApplyColliderStateToSpawnedObjects(bool isActive, bool log)
    {
        int affectedObjects = 0;
        foreach (var obj in spawnedObjects)
        {
            if (obj == null)
                continue;

            ApplyColliderStateToObject(obj, isActive);
            affectedObjects++;
        }

        if (log)
            Debug.Log($"[JsonSpawner] Colliders set to {isActive} for {affectedObjects} spawned objects");
    }

    private static void ApplyColliderStateToObject(GameObject obj, bool isActive)
    {
        if (obj == null)
            return;

        Collider[] cols = obj.GetComponentsInChildren<Collider>(true);
        foreach (var col in cols)
            col.enabled = isActive;
    }

    public void ClearSpawnedObjects()
    {
        if (infoPanel != null)
            infoPanel.ClearMarkers();

        foreach (var line in activeLines)
            if (line.createdMaterial != null)
                SafeDestroy(line.createdMaterial);

        HashSet<GameObject> toDestroy = new HashSet<GameObject>();

        foreach (var obj in spawnedObjects)
            if (obj != null)
                toDestroy.Add(obj);

        foreach (var mapping in nodeMappings)
            if (mapping?.node != null)
                toDestroy.Add(mapping.node.gameObject);

        foreach (var line in activeLines)
            if (line?.renderer != null)
                toDestroy.Add(line.renderer.gameObject);

        Transform parent = mapTransform != null ? mapTransform : transform;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child == null)
                continue;

            if (ShouldDestroyMapChild(child.gameObject))
                toDestroy.Add(child.gameObject);
        }

        foreach (GameObject go in toDestroy)
            if (go != null)
                SafeDestroy(go);

        spawnedObjects.Clear();
        activeLines.Clear();
        nodeMappings.Clear();
        lineNodeColorHexByNode.Clear();
        activeLinesByObjectName.Clear();

        if (toDestroy.Count > 0)
            Debug.Log($"[JsonSpawner] Cleared {toDestroy.Count} runtime map objects.");
    }

    private static bool ShouldDestroyMapChild(GameObject child)
    {
        if (child == null)
            return false;

        if (child.GetComponent<PointData>() != null)
            return true;

        if (child.GetComponent<RuntimeLine>() != null)
            return true;

        return child.name.Contains("(Clone)") ||
               child.name.StartsWith("Line_", StringComparison.Ordinal) ||
               child.name.StartsWith("Centroid_", StringComparison.Ordinal) ||
               child.name.StartsWith("Corner_", StringComparison.Ordinal);
    }

    private static void SafeDestroy(UnityEngine.Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }

    // Filters
    public void FilterAnimals(bool isVisible)  => SetCategoryVisibility("animal", isVisible);
    public void FilterCitizens(bool isVisible) => SetCategoryVisibility("citizen", isVisible);
    public void FilterDebris(bool isVisible) => SetCategoryVisibility("debris", isVisible);
    public void FilterHoles(bool isVisible)  => SetCategoryVisibility("holes", isVisible);
    public void FilterBarrels(bool isVisible) => SetCategoryVisibility("barrel", isVisible);
    public void FilterFires(bool isVisible) => SetCategoryVisibility("fire", isVisible);
    public void FilterSmokes(bool isVisible)  => SetCategoryVisibility("smoke", isVisible);
    public void FilterBarriers(bool isVisible) => SetCategoryVisibility("barrier", isVisible);
    public void FilterMuds(bool isVisible) => SetCategoryVisibility("mud", isVisible);
    public void FilterWater(bool isVisible) => SetCategoryVisibility("water", isVisible);



    public void SetCategoryVisibility(string targetCategory, bool isVisible)
    {
        if (string.IsNullOrEmpty(targetCategory)) return;

        string targetNorm = NormalizeCategoryToken(targetCategory);
        if (string.IsNullOrEmpty(targetNorm))
            return;

        HashSet<string> targetTokens = BuildCategoryTokens(targetNorm);

        foreach (var mapping in nodeMappings)
        {
            if (mapping.node == null || mapping.parentFeature == null || mapping.parentFeature.properties == null) continue;

            string type      = mapping.parentFeature.geometry?.type;
            string category  = mapping.parentFeature.properties.category ?? "";
            string className = mapping.parentFeature.properties.className ?? "";

            if (type == "Point" || type == "MultiPoint" || type == "Polygon" || type == "MultiPolygon")
            {
                string categoryNorm = NormalizeCategoryToken(category);
                string classNorm = NormalizeCategoryToken(className);

                if (MatchesCategory(targetTokens, categoryNorm) || MatchesCategory(targetTokens, classNorm))
                    mapping.node.gameObject.SetActive(isVisible);
            }
        }
    }

    private static bool MatchesCategory(HashSet<string> targetTokens, string candidateToken)
    {
        if (targetTokens == null || targetTokens.Count == 0 || string.IsNullOrEmpty(candidateToken))
            return false;

        foreach (string token in targetTokens)
        {
            if (candidateToken.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static HashSet<string> BuildCategoryTokens(string token)
    {
        HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(token))
            return tokens;

        tokens.Add(token);

        if (token.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && token.Length > 3)
            tokens.Add(token.Substring(0, token.Length - 3) + "y");

        if (token.EndsWith("y", StringComparison.OrdinalIgnoreCase) && token.Length > 1)
            tokens.Add(token.Substring(0, token.Length - 1) + "ies");

        if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase) && token.Length > 1)
            tokens.Add(token.Substring(0, token.Length - 1));
        else
            tokens.Add(token + "s");

        return tokens;
    }

    private static string NormalizeCategoryToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }

    public bool DeleteFeatureByNode(Transform node, bool requireDrawMode = false)
    {
        if (node == null) { Debug.LogWarning("[JsonSpawner] DeleteFeatureByNode: node is null."); return false; }

        if (menuButtonController == null)
            menuButtonController = FindFirstObjectByType<MenuButtonController>();

        if (requireDrawMode && (menuButtonController == null || !menuButtonController.IsDrawModeActive()))
        {
            Debug.LogWarning("[JsonSpawner] Delete blocked because draw mode is not active.");
            return false;
        }

        NodeMapping mapping = nodeMappings.Find(m => m.node == node);

        if (IsProtectedLatestMqttNode(node, mapping))
        {
            Debug.LogWarning("[JsonSpawner] Delete blocked for protected MQTT marker.");
            return false;
        }

        Debug.Log($"[Delete] Looking for: {node.name}");
        Debug.Log($"[Delete] Found mapping: {(mapping != null ? mapping.parentFeature?.id : "NULL")}");
    
        if (mapping == null)
        {
            PointData pd = node.GetComponentInParent<PointData>();
            Debug.Log($"[Delete] PointData found: {(pd != null ? pd.pointID : "NULL")}");

            string featureId = pd != null ? pd.pointID : null;
            bool removedFeature = RemoveFeatureById(featureId);
            bool removedStroke = DeleteLinkedStrokeForNode(node, featureId);

            if (removedFeature || removedStroke)
            {
                SaveToJson();
                Debug.Log($"[JsonSpawner] Deleted unmapped line/feature (featureId: {featureId ?? "-"}).");
                return true;
            }
            
            if (node != null)
            {
                spawnedObjects.Remove(node.gameObject);
                Destroy(node.gameObject);
                Debug.Log($"[JsonSpawner] Deleted orphaned object: {node.name}");
            }
            return true;
        }

        Feature featureToDelete = mapping.parentFeature;

        if (currentRootData != null && currentRootData.features != null)
            currentRootData.features.Remove(featureToDelete);

        List<NodeMapping> relatedMappings = nodeMappings.FindAll(m => m.parentFeature == featureToDelete);
        HashSet<string> candidateLineNames = new HashSet<string>(StringComparer.Ordinal);

        string mappedClassName = featureToDelete.properties?.className;
        string mappedFeatureId = featureToDelete.id;
        if (!string.IsNullOrEmpty(mappedClassName) && !string.IsNullOrEmpty(mappedFeatureId))
            candidateLineNames.Add($"Line_{mappedClassName}_{mappedFeatureId}");

        foreach (var m in relatedMappings)
        {
            if (m.node != null)
            {
                string strokeName = TryExtractStrokeName(m.node.name);
                if (!string.IsNullOrEmpty(strokeName))
                    candidateLineNames.Add(strokeName);

                if (infoPanel != null)
                {
                    PointData data = m.node.GetComponent<PointData>();
                    if (data != null) infoPanel.UnregisterMarker(data);
                }

                spawnedObjects.Remove(m.node.gameObject);
                Destroy(m.node.gameObject);
            }
        }
        
        nodeMappings.RemoveAll(m => m.parentFeature == featureToDelete);
        
        for (int i = activeLines.Count - 1; i >= 0; i--)
        {
            if (activeLines[i].renderer != null && activeLines[i].renderer.gameObject != null &&
                (activeLines[i].parentFeature == featureToDelete ||
                 candidateLineNames.Contains(activeLines[i].renderer.gameObject.name)))
            {
                if (activeLines[i].nodes != null)
                    foreach (Transform lineNode in activeLines[i].nodes)
                        RemoveLineNodeColorCache(lineNode);

                if (activeLines[i].createdMaterial != null)
                    Destroy(activeLines[i].createdMaterial);

                UnregisterActiveLineByName(activeLines[i].renderer.gameObject.name);
                spawnedObjects.Remove(activeLines[i].renderer.gameObject);
                Destroy(activeLines[i].renderer.gameObject);
                activeLines.RemoveAt(i);
            }
        }

        DestroyUntrackedLineRenderersByName(candidateLineNames);
        

        SaveToJson();
        Debug.Log($"[JsonSpawner] Feature with ID: {featureToDelete.id} deleted and JSON saved.");
        return true;
    }

    private bool RemoveFeatureById(string featureId)
    {
        if (string.IsNullOrEmpty(featureId) || currentRootData?.features == null)
            return false;

        int removedCount = currentRootData.features.RemoveAll(f =>
            string.Equals(f.id, featureId, StringComparison.OrdinalIgnoreCase));

        return removedCount > 0;
    }

    private bool DeleteLinkedStrokeForNode(Transform node, string featureId)
    {
        if (node == null) return false;

        if (IsProtectedLatestMqttNode(node, null, featureId))
            return false;

        bool deletedSomething = false;
        PointData nodeData = node.GetComponentInParent<PointData>();
        string className = nodeData != null ? nodeData.pointClass : null;

        if (!string.IsNullOrEmpty(featureId))
        {
            PointData[] allPoints = FindObjectsByType<PointData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var point in allPoints)
            {
                if (point == null || point.gameObject == null) continue;
                if (!string.Equals(point.pointID, featureId, StringComparison.OrdinalIgnoreCase)) continue;

                if (infoPanel != null)
                    infoPanel.UnregisterMarker(point);

                spawnedObjects.Remove(point.gameObject);
                Destroy(point.gameObject);
                deletedSomething = true;
            }
        }
        else
        {
            PointData pd = node.GetComponentInParent<PointData>();
            if (pd != null && infoPanel != null)
                infoPanel.UnregisterMarker(pd);

            spawnedObjects.Remove(node.gameObject);
            Destroy(node.gameObject);
            deletedSomething = true;
        }

        HashSet<string> candidateLineNames = new HashSet<string>(StringComparer.Ordinal);

        string strokeName = TryExtractStrokeName(node.name);
        if (!string.IsNullOrEmpty(strokeName))
            candidateLineNames.Add(strokeName);

        if (!string.IsNullOrEmpty(featureId) && !string.IsNullOrEmpty(className))
            candidateLineNames.Add($"Line_{className}_{featureId}");

        for (int i = activeLines.Count - 1; i >= 0; i--)
        {
            if (activeLines[i].renderer == null || activeLines[i].renderer.gameObject == null) continue;

            if (!string.IsNullOrEmpty(featureId) && activeLines[i].parentFeature != null)
            {
                string lineFeatureId = activeLines[i].parentFeature.id;

                if (string.Equals(lineFeatureId, featureId, StringComparison.OrdinalIgnoreCase))
                {
                    if (activeLines[i].createdMaterial != null)
                        Destroy(activeLines[i].createdMaterial);

                    if (activeLines[i].nodes != null)
                        foreach (Transform lineNode in activeLines[i].nodes)
                            RemoveLineNodeColorCache(lineNode);

                    string matchedLineName = activeLines[i].renderer.gameObject.name;
                    spawnedObjects.Remove(activeLines[i].renderer.gameObject);
                    Destroy(activeLines[i].renderer.gameObject);
                    UnregisterActiveLineByName(matchedLineName);
                    activeLines.RemoveAt(i);
                    deletedSomething = true;
                    continue;
                }
            }

            string lineObjName = activeLines[i].renderer.gameObject.name;
            if (!candidateLineNames.Contains(lineObjName)) continue;

            if (activeLines[i].createdMaterial != null)
                Destroy(activeLines[i].createdMaterial);

            if (activeLines[i].nodes != null)
                foreach (Transform lineNode in activeLines[i].nodes)
                    RemoveLineNodeColorCache(lineNode);

            spawnedObjects.Remove(activeLines[i].renderer.gameObject);
            Destroy(activeLines[i].renderer.gameObject);
            UnregisterActiveLineByName(lineObjName);
            activeLines.RemoveAt(i);
            deletedSomething = true;
        }

        if (!deletedSomething && candidateLineNames.Count > 0)
        {
            foreach (string candidateLineName in candidateLineNames)
            {
                if (!activeLinesByObjectName.TryGetValue(candidateLineName, out LineData lineData) ||
                    lineData == null || lineData.renderer == null || lineData.renderer.gameObject == null)
                    continue;

                if (lineData.createdMaterial != null)
                    Destroy(lineData.createdMaterial);

                if (lineData.nodes != null)
                    foreach (Transform lineNode in lineData.nodes)
                        RemoveLineNodeColorCache(lineNode);

                spawnedObjects.Remove(lineData.renderer.gameObject);
                Destroy(lineData.renderer.gameObject);
                activeLines.Remove(lineData);
                UnregisterActiveLineByName(candidateLineName);
                deletedSomething = true;
            }
        }

        if (DestroyUntrackedLineRenderersByName(candidateLineNames))
            deletedSomething = true;

        return deletedSomething;
    }

    private static bool IsProtectedLatestMqttNode(Transform node, NodeMapping mapping, string featureId = null)
    {
        if (node == null)
            return false;

        if (string.Equals(node.name, "LatestMqtt_" + LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase))
            return true;

        PointData pointData = node.GetComponentInParent<PointData>();
        if (pointData != null)
        {
            if (string.Equals(pointData.pointID, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(pointData.pointClass, LatestMqttClassName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        Feature feature = mapping != null ? mapping.parentFeature : null;
        if (feature != null)
        {
            if (string.Equals(feature.id, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (feature.properties != null && string.Equals(feature.properties.className, LatestMqttClassName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !string.IsNullOrWhiteSpace(featureId) && string.Equals(featureId, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase);
    }

    private bool DestroyUntrackedLineRenderersByName(HashSet<string> candidateLineNames)
    {
        if (candidateLineNames == null || candidateLineNames.Count == 0)
            return false;

        bool deletedAny = false;
        LineRenderer[] sceneLineRenderers = FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (LineRenderer lineRenderer in sceneLineRenderers)
        {
            if (lineRenderer == null || lineRenderer.gameObject == null)
                continue;

            string lineName = lineRenderer.gameObject.name;
            if (!candidateLineNames.Contains(lineName))
                continue;

            spawnedObjects.Remove(lineRenderer.gameObject);
            UnregisterActiveLineByName(lineName);
            Destroy(lineRenderer.gameObject);
            deletedAny = true;
        }

        return deletedAny;
    }

    private string TryExtractStrokeName(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName) || !nodeName.StartsWith("Node_", StringComparison.Ordinal))
            return null;

        string withoutPrefix = nodeName.Substring("Node_".Length);
        int lastUnderscore = withoutPrefix.LastIndexOf('_');
        if (lastUnderscore <= 0)
            return null;

        string possibleIndex = withoutPrefix.Substring(lastUnderscore + 1);
        if (!int.TryParse(possibleIndex, out _))
            return null;

        return withoutPrefix.Substring(0, lastUnderscore);
    }

    public void SpawnPrefabAtOrigin(string className)
    {
        if (!TryGetPrefabForClass(className, out GameObject prefab) || prefab == null)
        {
            Debug.LogWarning($"[JsonSpawner] Prefab not found for class: {className}");
            return;
        }

        Transform parentRef = mapTransform != null ? mapTransform : transform;

        Vector3 spawnPos = Camera.main != null
            ? Camera.main.transform.position + Camera.main.transform.forward * 0.5f
            : parentRef.position;

        GameObject obj = Instantiate(prefab);
        obj.transform.SetParent(parentRef, false);
        obj.transform.localScale = prefab.transform.localScale;
        EnsurePositiveScaleForBoxCollider(obj);
        obj.transform.position = spawnPos;

        string newId = AllocateNextFeatureId();

        obj.name = $"{className}_{newId}";
        obj.tag = "MapMarker";

        Vector3 localPos = parentRef.InverseTransformPoint(spawnPos);
        Vector3Double wgs = ColmapToWgs84(localPos);

        PointData data = obj.GetComponent<PointData>();
        if (data == null) data = obj.AddComponent<PointData>();
        data.pointClass = className;
        data.pointID    = newId;
        data.category   = className;
        data.source     = "Ground Station";
        data.latitude   = wgs.lat;
        data.longitude  = wgs.lon;
        data.altitude   = wgs.alt;
        data.confidence = 1f;

        Collider[] cols = obj.GetComponentsInChildren<Collider>(true);
        foreach (var col in cols) col.enabled = collidersDesiredActive;

        spawnedObjects.Add(obj);
        SetupIcon(obj);
        RegisterToInfoPanel(obj);

        JArray coordsArray = new JArray { wgs.lon, wgs.lat, wgs.alt };

        Feature newFeature = new Feature
        {
            type = "Feature",
            id = newId,
            properties = new Properties
            {
                className  = className,
                id         = newId,
                confidence = 1f,
                category   = className,
                source     = "Ground Station",
                altitude_m = (float)wgs.alt,
                marker_color = "#ff0000"
            },
            geometry = new Geometry
            {
                type        = "Point",
                coordinates = coordsArray
            }
        };

        lock (dataMutationLock)
        {
            EnsureRootDataInitializedLocked();
            currentRootData.features.Add(newFeature);
            reservedFeatureIds.Remove(newId);
        }

        nodeMappings.Add(new NodeMapping
        {
            node          = obj.transform,
            coordArray    = coordsArray,
            parentFeature = newFeature,
            isCentroid    = false,
            parentCentroid = null
        });

        SaveToJson();

        if (infoPanel != null)
        {
            PointData pd = obj.GetComponent<PointData>();
            if (pd != null)
                MarkerEventManager.RaiseMarkerSelected(pd);
        }

        Debug.Log($"[SpawnPrefab] Successfully spawned {className} (ID: {newId}) as Point and saved.");
    }

    private static void EnsurePositiveScaleForBoxCollider(GameObject obj)
    {
        if (obj == null)
            return;

        BoxCollider[] colliders = obj.GetComponentsInChildren<BoxCollider>(true);
        if (colliders == null || colliders.Length == 0)
            return;

        Vector3 local = obj.transform.localScale;
        Vector3 safe = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

        if (!Mathf.Approximately(local.x, safe.x) || !Mathf.Approximately(local.y, safe.y) || !Mathf.Approximately(local.z, safe.z))
            obj.transform.localScale = safe;
    }

    private static string ResolveLineColorHex(Feature feature, Color fallbackColor)
    {
        string markerHex = feature?.properties?.marker_color;
        if (!string.IsNullOrEmpty(markerHex) && ColorUtility.TryParseHtmlString(markerHex, out _))
            return markerHex.StartsWith("#", StringComparison.Ordinal) ? markerHex : "#" + markerHex;

        string className = feature?.properties?.className ?? string.Empty;
        if (TryGetColorFromClassName(className, out Color fromClassName))
            return "#" + ColorUtility.ToHtmlStringRGB(fromClassName);

        return "#" + ColorUtility.ToHtmlStringRGB(fallbackColor);
    }

    private static bool TryGetColorFromClassName(string className, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(className))
            return false;

        string token = className.Trim();
        if (token.StartsWith("Drawing_", StringComparison.OrdinalIgnoreCase))
            token = token.Substring("Drawing_".Length);
        else if (token.StartsWith("Line_", StringComparison.OrdinalIgnoreCase))
            token = token.Substring("Line_".Length);

        token = token.Replace("-", " ").Replace("_", " ").Trim().ToLowerInvariant();

        if (token.Contains("red")) { color = Color.red; return true; }
        if (token.Contains("yellow")) { color = Color.yellow; return true; }
        if (token.Contains("blue")) { color = Color.blue; return true; }
        if (token.Contains("green")) { color = Color.green; return true; }
        if (token.Contains("lime")) { color = new Color(0.5f, 1f, 0f); return true; }
        if (token.Contains("cyan")) { color = Color.cyan; return true; }
        if (token.Contains("magenta")) { color = Color.magenta; return true; }
        if (token.Contains("purple")) { color = new Color(0.5f, 0f, 1f); return true; }
        if (token.Contains("orange")) { color = new Color(1f, 0.5f, 0f); return true; }
        if (token.Contains("pink")) { color = new Color(1f, 0.4f, 0.7f); return true; }
        if (token.Contains("white")) { color = Color.white; return true; }

        return false;
    }

    public string AddPointFeatureAndSave(GameObject obj, string className, string source = "Ground Station")
    {
        if (obj == null)
            return string.Empty;

        Transform parentRef = mapTransform != null ? mapTransform : transform;
        Vector3 localPos    = parentRef.InverseTransformPoint(obj.transform.position);
        Vector3Double wgs   = ColmapToWgs84(localPos);

        string newId = AllocateNextFeatureId();

        PointData data = obj.GetComponent<PointData>();
        if (data == null) data = obj.AddComponent<PointData>();
        data.pointClass = className;
        data.pointID    = newId;
        data.category   = className;
        data.source     = source;
        data.latitude   = wgs.lat;
        data.longitude  = wgs.lon;
        data.altitude   = wgs.alt;
        data.confidence = 1f;

        JArray coordsArray = new JArray { wgs.lon, wgs.lat, wgs.alt };

        Feature newFeature = new Feature
        {
            type = "Feature",
            id   = newId,
            properties = new Properties
            {
                className  = className,
                id         = newId,
                confidence = 1f,
                category   = className,
                source     = source,
                altitude_m = (float)wgs.alt,
                marker_color = "#ff0000"
            },
            geometry = new Geometry { type = "Point", coordinates = coordsArray }
        };

        lock (dataMutationLock)
        {
            EnsureRootDataInitializedLocked();
            currentRootData.features.Add(newFeature);
            reservedFeatureIds.Remove(newId);
        }

        nodeMappings.Add(new NodeMapping
        {
            node           = obj.transform,
            coordArray     = coordsArray,
            parentFeature  = newFeature,
            isCentroid     = false,
            parentCentroid = null
        });

        SaveToJson();
        Debug.Log($"[JsonSpawner] AddPointFeatureAndSave: {className} ID={newId}");
        return newId;
    }

    public string AddLineFeatureAndSave(List<Vector3> worldPositions, string className, Color color, string source = "Ground Station")
    {
        if (worldPositions == null || worldPositions.Count < 2) return null;

        Transform parentRef = mapTransform != null ? mapTransform : transform;

        string newId = AllocateNextFeatureId();

        JArray coords = new JArray();
        double firstAlt = 0;
        foreach (var wp in worldPositions)
        {
            Vector3 localPos  = parentRef.InverseTransformPoint(wp);
            Vector3Double wgs = ColmapToWgs84(localPos);
            coords.Add(new JArray { wgs.lon, wgs.lat, wgs.alt });
            if (firstAlt == 0) firstAlt = wgs.alt;
        }

        string colorHex = "#" + ColorUtility.ToHtmlStringRGB(color);
        const string lineClassName = "road";
        const string lineCategory = "navigation";

        Feature newFeature = new Feature
        {
            type = "Feature",
            id   = newId,
            properties = new Properties
            {
                className  = lineClassName,
                id         = newId,
                confidence = 1f,
                category   = lineCategory,
                source     = source,
                altitude_m = (float)firstAlt,
                marker_color = colorHex
            },
            geometry = new Geometry { type = "LineString", coordinates = coords }
        };

        lock (dataMutationLock)
        {
            EnsureRootDataInitializedLocked();
            currentRootData.features.Add(newFeature);
            reservedFeatureIds.Remove(newId);
        }

        SaveToJson();
        Debug.Log($"[JsonSpawner] AddLineFeatureAndSave: {lineClassName} ID={newId} ({worldPositions.Count} points)");
        return newId;
    }

    public string AddPolygonFeatureAndSave(List<Vector3> worldPositions, string className, string category, Color color, string source = "Ground Station")
    {
        if (worldPositions == null || worldPositions.Count < 3) return null;

        Transform parentRef = mapTransform != null ? mapTransform : transform;

        string newId = AllocateNextFeatureId();

        JArray ring = new JArray();
        double firstAlt = 0d;

        for (int i = 0; i < worldPositions.Count; i++)
        {
            Vector3 localPos = parentRef.InverseTransformPoint(worldPositions[i]);
            Vector3Double wgs = ColmapToWgs84(localPos);
            ring.Add(new JArray { wgs.lon, wgs.lat, wgs.alt });

            if (i == 0)
                firstAlt = wgs.alt;
        }

        JArray firstCoord = ring[0] as JArray;
        JArray lastCoord = ring[ring.Count - 1] as JArray;
        bool alreadyClosed = firstCoord != null && lastCoord != null &&
                             firstCoord.Count >= 2 && lastCoord.Count >= 2 &&
                             Mathf.Approximately((float)firstCoord[0].Value<double>(), (float)lastCoord[0].Value<double>()) &&
                             Mathf.Approximately((float)firstCoord[1].Value<double>(), (float)lastCoord[1].Value<double>());

        if (!alreadyClosed && firstCoord != null)
            ring.Add(new JArray { firstCoord[0].Value<double>(), firstCoord[1].Value<double>(), firstCoord.Count >= 3 ? firstCoord[2].Value<double>() : firstAlt });

        JArray polygonCoords = new JArray { ring };

        string colorHex = "#" + ColorUtility.ToHtmlStringRGB(color);
        string normalizedClass = string.Equals(className, "safe", StringComparison.OrdinalIgnoreCase) ? "safe" : "unsafe";
        const string normalizedCategory = "navigation";

        Feature newFeature = new Feature
        {
            type = "Feature",
            id = newId,
            properties = new Properties
            {
                className = normalizedClass,
                id = newId,
                confidence = 1f,
                category = normalizedCategory,
                source = source,
                altitude_m = (float)firstAlt,
                marker_color = colorHex,
                fill = colorHex,
                fill_opacity = 0.25f
            },
            geometry = new Geometry { type = "Polygon", coordinates = polygonCoords }
        };

        lock (dataMutationLock)
        {
            EnsureRootDataInitializedLocked();
            currentRootData.features.Add(newFeature);
            reservedFeatureIds.Remove(newId);
        }

        SaveToJson();
        Debug.Log($"[JsonSpawner] AddPolygonFeatureAndSave: {normalizedClass} ({normalizedCategory}) ID={newId} ({worldPositions.Count} points)");
        return newId;
    }

    private string AllocateNextFeatureId()
    {
        lock (dataMutationLock)
        {
            EnsureRootDataInitializedLocked();

            HashSet<string> existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var feature in currentRootData.features)
            {
                if (!string.IsNullOrEmpty(feature?.id))
                    existingIds.Add(feature.id);
            }

            foreach (string reservedId in reservedFeatureIds)
                existingIds.Add(reservedId);

            string allocatedId;
            do
            {
                allocatedId = "feature_" + Guid.NewGuid().ToString("N");
            }
            while (existingIds.Contains(allocatedId));

            reservedFeatureIds.Add(allocatedId);
            return allocatedId;
        }
    }

    private void EnsureRootDataInitializedLocked()
    {
        if (currentRootData == null)
            currentRootData = new Root { type = "FeatureCollection", features = new List<Feature>() };

        if (currentRootData.features == null)
            currentRootData.features = new List<Feature>();
    }

    public bool RegisterRuntimeLineNodes(string featureId, List<Transform> nodes)
    {
        if (string.IsNullOrEmpty(featureId) || nodes == null || nodes.Count == 0)
            return false;

        if (currentRootData?.features == null)
            return false;

        Feature feature = currentRootData.features.Find(f =>
            string.Equals(f.id, featureId, StringComparison.OrdinalIgnoreCase));

        if (feature == null)
            return false;

        JArray coords = feature.geometry?.coordinates as JArray;
        if (coords == null)
            return false;

        int registered = 0;
        int maxCount = Math.Min(nodes.Count, coords.Count);
        string resolvedLineColorHex = ResolveLineColorHexFromFeatureOrNodes(feature, nodes, Color.red);

        for (int i = 0; i < maxCount; i++)
        {
            Transform node = nodes[i];
            if (node == null) continue;

            JArray coordArray = coords[i] as JArray;
            if (coordArray == null || coordArray.Count < 3) continue;

            nodeMappings.RemoveAll(m => m.node == node);
            nodeMappings.Add(new NodeMapping
            {
                node = node,
                coordArray = coordArray,
                parentFeature = feature,
                isCentroid = false,
                parentCentroid = null
            });

            PointData data = node.GetComponent<PointData>();
            if (data == null) data = node.gameObject.AddComponent<PointData>();

            data.lineColorHex = resolvedLineColorHex;
            data.pointClass = feature.properties?.className ?? "road";
            data.pointID = feature.id ?? featureId;
            data.category = feature.properties?.category ?? "navigation";
            data.source = feature.properties?.source ?? "Ground Station";
            data.longitude = coordArray[0].Value<double>();
            data.latitude = coordArray[1].Value<double>();
            data.altitude = coordArray[2].Value<double>();
            data.confidence = feature.properties?.confidence ?? 1f;

            CacheLineNodeColorHex(node, resolvedLineColorHex);

            registered++;
        }

        if (registered > 0)
            Debug.Log($"[JsonSpawner] RegisterRuntimeLineNodes: feature {featureId}, nodes registered={registered}");

        return registered > 0;
    }

    public bool RegisterRuntimePolygonNodes(string featureId, List<Transform> nodes)
    {
        if (string.IsNullOrEmpty(featureId) || nodes == null || nodes.Count == 0)
            return false;

        if (currentRootData?.features == null)
            return false;

        Feature feature = currentRootData.features.Find(f =>
            string.Equals(f.id, featureId, StringComparison.OrdinalIgnoreCase));

        if (feature == null)
            return false;

        JArray polygon = feature.geometry?.coordinates as JArray;
        if (polygon == null || polygon.Count == 0)
            return false;

        JArray outerRing = polygon[0] as JArray;
        if (outerRing == null || outerRing.Count == 0)
            return false;

        int ringUsableCount = outerRing.Count;
        if (outerRing.Count > 1 &&
            outerRing[0] is JArray first && outerRing[outerRing.Count - 1] is JArray last &&
            first.Count >= 2 && last.Count >= 2 &&
            Mathf.Approximately((float)first[0].Value<double>(), (float)last[0].Value<double>()) &&
            Mathf.Approximately((float)first[1].Value<double>(), (float)last[1].Value<double>()))
            ringUsableCount = outerRing.Count - 1;

        int registered = 0;
        int maxCount = Math.Min(nodes.Count, ringUsableCount);
        string resolvedLineColorHex = ResolveLineColorHexFromFeatureOrNodes(feature, nodes, Color.red);

        for (int i = 0; i < maxCount; i++)
        {
            Transform node = nodes[i];
            if (node == null) continue;

            JArray coordArray = outerRing[i] as JArray;
            if (coordArray == null || coordArray.Count < 3) continue;

            nodeMappings.RemoveAll(m => m.node == node);
            nodeMappings.Add(new NodeMapping
            {
                node = node,
                coordArray = coordArray,
                parentFeature = feature,
                isCentroid = false,
                parentCentroid = null
            });

            PointData data = node.GetComponent<PointData>();
            if (data == null) data = node.gameObject.AddComponent<PointData>();

            data.lineColorHex = resolvedLineColorHex;
            data.pointClass = feature.properties?.className ?? "unsafe";
            data.pointID = feature.id ?? featureId;
            data.category = feature.properties?.category ?? "navigation";
            data.source = feature.properties?.source ?? "Ground Station";
            data.longitude = coordArray[0].Value<double>();
            data.latitude = coordArray[1].Value<double>();
            data.altitude = coordArray[2].Value<double>();
            data.confidence = feature.properties?.confidence ?? 1f;

            CacheLineNodeColorHex(node, resolvedLineColorHex);
            registered++;
        }

        if (registered > 0)
            Debug.Log($"[JsonSpawner] RegisterRuntimePolygonNodes: feature {featureId}, nodes registered={registered}");

        return registered > 0;
    }

    private string ResolveLineColorHexFromFeatureOrNodes(Feature feature, List<Transform> nodes, Color fallbackColor)
    {
        if (nodes != null)
        {
            foreach (Transform node in nodes)
            {
                if (node == null)
                    continue;

                if (lineNodeColorHexByNode.TryGetValue(node, out string cachedHex) &&
                    !string.IsNullOrEmpty(cachedHex) &&
                    ColorUtility.TryParseHtmlString(cachedHex, out _))
                    return cachedHex;
            }
        }

        return ResolveLineColorHex(feature, fallbackColor);
    }

    private string ResolveLineColorHexFromFeatureOrCache(Feature feature, Transform node, Color fallbackColor)
    {
        if (node != null && lineNodeColorHexByNode.TryGetValue(node, out string cachedHex) &&
            !string.IsNullOrEmpty(cachedHex) && ColorUtility.TryParseHtmlString(cachedHex, out _))
            return cachedHex;

        return ResolveLineColorHex(feature, fallbackColor);
    }

    private void CacheLineNodeColorHex(Transform node, string hexColor)
    {
        if (node == null)
            return;

        if (string.IsNullOrEmpty(hexColor) || !ColorUtility.TryParseHtmlString(hexColor, out _))
            return;

        lineNodeColorHexByNode[node] = hexColor;
    }

    private void RemoveLineNodeColorCache(Transform node)
    {
        if (node == null)
            return;

        lineNodeColorHexByNode.Remove(node);
    }
}
