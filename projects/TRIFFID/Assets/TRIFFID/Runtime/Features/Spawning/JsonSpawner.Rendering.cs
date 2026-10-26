using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;

// Implementation of the existing JsonSpawner component. No additional scene component.
public partial class JsonSpawner
{
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

    private bool HasSupportedPolygonClass(Feature feature, out string className)
    {
        className = feature?.properties?.className?.Trim() ?? string.Empty;

        if (XRControllerLogger.TryNormalizePolygonClass(className, out string drawableClass))
        {
            className = drawableClass;
            return true;
        }

        return TryGetPrefabForClass(className, out _);
    }

    private static string NormalizePrefabToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }

    private bool TryPreflightSpawnInputs(out TransformConfig loadedTransform, out Root loadedRoot, out string loadPath)
    {
        loadedTransform = null;
        loadedRoot = null;
        loadPath = string.Empty;

        string resolvedTransformPath = ResolveTransformJsonPathForRuntime();
        if (string.IsNullOrWhiteSpace(resolvedTransformPath))
            return false;

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

        bool isPolygonGeometry =
            string.Equals(geom.type, "Polygon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(geom.type, "MultiPolygon", StringComparison.OrdinalIgnoreCase);

        if (isPolygonGeometry && !HasSupportedPolygonClass(feature, out string polygonClassName))
        {
            string displayedClass = string.IsNullOrWhiteSpace(polygonClassName) ? "<missing>" : polygonClassName;
            string featureId = feature?.id ?? feature?.properties?.id ?? "<missing>";
            Debug.LogWarning($"[JsonSpawner] Skipping {geom.type} feature '{featureId}': unsupported class '{displayedClass}'.");
            return;
        }

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
        // altitude_m is a point altitude. On lines/polygons it may summarize a
        // different vertex and cannot resolve this vertex's missing elevation.
        bool hasAltitudeFromProperties = IsPointFeature(feature) && (feature.properties?.altitude_m.HasValue ?? false);

        double alt = hasAltitudeFromProperties ? feature.properties.altitude_m.Value : fallbackAltitude ?? GetTransformOriginAltitude();
        if (hasAltitudeFromToken)
            alt = coordAlt;

        bool missingAltitude = !hasAltitudeFromToken && !hasAltitudeFromProperties;
        bool shouldFallbackToSurface = enableSurfaceSnapFallback && missingAltitude;

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

            bool elevationResolved = !missingAltitude;
            if (shouldFallbackToSurface && TrySnapToSurface(obj.transform.position, parentRef, out Vector3 snappedWorldPos))
            {
                elevationResolved = true;
                obj.transform.position = snappedWorldPos;

                Vector3 snappedLocalPos = parentRef.InverseTransformPoint(snappedWorldPos);
                Vector3Double snappedWgs = ColmapToWgs84(snappedLocalPos);
                alt = snappedWgs.alt;
                // Reproject using the supplied longitude/latitude. A surface
                // estimate may supply altitude, never replacement horizontal coordinates.
                obj.transform.localPosition = GetColmapPosition(lon, lat, alt);
            }
            else if (shouldFallbackToSurface)
            {
                Debug.LogWarning($"[JsonSpawner] Surface snap failed for {className}_{id} (missing/null altitude). Elevation unavailable. Displaying a flagged preview at altitude={alt:F3}.");
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
            if (prefabToUse == nodePrefab) ApplyColor(obj, fallbackColor);
            data.SetElevationUnresolved(!elevationResolved);

            if (isCentroid)
            {
                // A centroid can show a live value in the InfoPanel, but it is not
                // part of the per-vertex GeoJSON height array.
                RefreshHeightAboveSurface(obj.transform, null, data);
            }
            else if (IsPointFeature(feature))
            {
                RefreshHeightAboveSurface(obj.transform, feature, data, true, false);
            }
            else if (SupportsPerVertexHeight(feature))
            {
                bool hadStoredHeight = TryGetStoredVertexHeight(feature, token, out double storedHeight);
                RefreshHeightAboveSurface(obj.transform, null, data);

                if (!data.hasHeightAboveSurface && hadStoredHeight)
                {
                    data.heightAboveSurface = storedHeight;
                    data.hasHeightAboveSurface = true;
                    data.heightSource = "retained";
                    data.heightStatus = "query_failed";
                }

            }

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

            if (prefabToUse == nodePrefab && !data.elevationUnresolved) ApplyColor(obj, fallbackColor);

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
            ApplyFeatureVisibility(obj, feature);

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

    public void RegisterRuntimeStroke(RuntimeLine stroke)
    {
        runtimeAnnotationStrokes.RemoveAll(item => item == null);
        if (stroke != null && !runtimeAnnotationStrokes.Contains(stroke)) runtimeAnnotationStrokes.Add(stroke);
    }

    public void ClearSpawnedObjects() { ClearSpawnedObjects(false); }

    private static bool IsLatestMqttObject(GameObject obj)
    {
        PointData data = obj == null ? null : obj.GetComponent<PointData>();
        return data != null && string.Equals(data.pointID, LatestMqttFeatureId, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearSpawnedObjects(bool preserveRuntimeObjects)
    {
        if (infoPanel != null)
            infoPanel.ClearMarkers();

        foreach (var line in activeLines)
            if (line.createdMaterial != null)
                SafeDestroy(line.createdMaterial);

        HashSet<GameObject> toDestroy = new HashSet<GameObject>();

        foreach (var obj in spawnedObjects)
            if (obj != null && (!preserveRuntimeObjects || !IsLatestMqttObject(obj)))
                toDestroy.Add(obj);

        foreach (var mapping in nodeMappings)
            if (mapping?.node != null && (!preserveRuntimeObjects || !IsLatestMqttObject(mapping.node.gameObject)))
                toDestroy.Add(mapping.node.gameObject);

        foreach (var line in activeLines)
            if (line?.renderer != null)
                toDestroy.Add(line.renderer.gameObject);

        foreach (RuntimeLine stroke in runtimeAnnotationStrokes)
        {
            if (stroke == null || (preserveRuntimeObjects && stroke.IsDraft)) continue;
            stroke.ReleaseOwnedObjects();
            toDestroy.Add(stroke.gameObject);
        }

        Transform parent = mapTransform != null ? mapTransform : transform;
        for (int i = parent.childCount - 1; !preserveRuntimeObjects && i >= 0; i--)
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

        spawnedObjects.RemoveAll(obj => obj == null || toDestroy.Contains(obj));
        activeLines.Clear();
        nodeMappings.RemoveAll(mapping => mapping?.node == null || toDestroy.Contains(mapping.node.gameObject));
        runtimeAnnotationStrokes.RemoveAll(stroke => stroke == null || toDestroy.Contains(stroke.gameObject));
        lineNodeColorHexByNode.Clear();
        activeLinesByObjectName.Clear();

        // MQTT has its own poll and lifetime. Keep its object and re-register
        // its panel entry while GeoJSON annotations are rebuilt around it.
        if (preserveRuntimeObjects)
            foreach (GameObject obj in spawnedObjects)
                if (IsLatestMqttObject(obj)) RegisterToInfoPanel(obj);

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
        categoryVisibility.SetVisibility(targetCategory, isVisible);
        ApplyCurrentCategoryFilters();
    }

    private void ApplyFeatureVisibility(GameObject obj, Feature feature)
    {
        bool visible;
        if (obj != null && categoryVisibility.TryGetVisibility(feature, out visible) && obj.activeSelf != visible)
            obj.SetActive(visible);
    }

    private void ApplyCurrentCategoryFilters()
    {
        foreach (NodeMapping mapping in nodeMappings)
            if (mapping?.node != null) ApplyFeatureVisibility(mapping.node.gameObject, mapping.parentFeature);
        foreach (LineData line in activeLines)
            if (line?.renderer != null) ApplyFeatureVisibility(line.renderer.gameObject, line.parentFeature);
        foreach (RuntimeLine stroke in runtimeAnnotationStrokes)
        {
            if (stroke == null || string.IsNullOrEmpty(stroke.FeatureId) || currentRootData?.features == null) continue;
            Feature feature = currentRootData.features.Find(item => item != null &&
                string.Equals(item.id, stroke.FeatureId, StringComparison.OrdinalIgnoreCase));
            ApplyFeatureVisibility(stroke.gameObject, feature);
        }
    }
}

