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
        GeoJsonOutbox.RecordDeletion(currentRootData, featureToDelete);

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

        foreach (Feature deleted in currentRootData.features.FindAll(f =>
            string.Equals(f.id, featureId, StringComparison.OrdinalIgnoreCase)))
            GeoJsonOutbox.RecordDeletion(currentRootData, deleted);
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
        RefreshHeightAboveSurface(obj.transform, null, data);
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
                height_above_surface_m = data.hasHeightAboveSurface ? (float?)data.heightAboveSurface : null,
                heightSource = data.heightSource,
                heightStatus = data.heightStatus,
                elevationStatus = data.elevationUnresolved ? "unresolved" : "available",
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

        ApplyFeatureVisibility(obj, newFeature);
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
        RefreshHeightAboveSurface(obj.transform, null, data);
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
                height_above_surface_m = data.hasHeightAboveSurface ? (float?)data.heightAboveSurface : null,
                heightSource = data.heightSource,
                heightStatus = data.heightStatus,
                elevationStatus = data.elevationUnresolved ? "unresolved" : "available",
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

        ApplyFeatureVisibility(obj, newFeature);
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

        if (!XRControllerLogger.TryNormalizePolygonClass(className, out string normalizedClass))
        {
            Debug.LogWarning($"[JsonSpawner] Cannot save polygon: unsupported drawable class '{className ?? "<missing>"}'.");
            return null;
        }

        Transform parentRef = mapTransform != null ? mapTransform : transform;

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

        string validationError;
        if (!GeoJsonPolygonValidator.TryNormalizeRing(ring, true, out validationError))
        {
            ReportValidationError(validationError);
            return null;
        }
        LastValidationError = null;
        string newId = AllocateNextFeatureId();

        JArray polygonCoords = new JArray { ring };

        string colorHex = "#" + ColorUtility.ToHtmlStringRGB(color);
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
        GeoJsonOutbox.Initialize(currentRootData, false);
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
            RefreshHeightAboveSurface(node, null, data);

            CacheLineNodeColorHex(node, resolvedLineColorHex);

            registered++;
        }

        if (registered > 0)
        {
            UpdateFeatureHeightAboveSurfaceData(feature);
            MarkDataDirty();
            SaveCurrentStateToPersistentStorage();
            Debug.Log($"[JsonSpawner] RegisterRuntimeLineNodes: feature {featureId}, nodes registered={registered}");
        }

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
        if (ringUsableCount > 1 && GeoJsonPolygonValidator.SameHorizontal(outerRing[0] as JArray, outerRing[ringUsableCount - 1] as JArray))
            ringUsableCount--;
        HashSet<JArray> usedCoordinates = new HashSet<JArray>();
        Transform polygonMap = mapTransform != null ? mapTransform : transform;

        int registered = 0;
        int maxCount = Math.Min(nodes.Count, ringUsableCount);
        string resolvedLineColorHex = ResolveLineColorHexFromFeatureOrNodes(feature, nodes, Color.red);

        for (int i = 0; i < maxCount; i++)
        {
            Transform node = nodes[i];
            if (node == null) continue;

            Vector3Double nodeWgs = ColmapToWgs84(polygonMap.InverseTransformPoint(node.position));
            JArray coordArray = null;
            double nearestDistance = double.MaxValue;
            for (int coordinateIndex = 0; coordinateIndex < ringUsableCount; coordinateIndex++)
            {
                JArray candidate = outerRing[coordinateIndex] as JArray;
                if (candidate == null || candidate.Count < 3 || usedCoordinates.Contains(candidate)) continue;
                double dx = candidate[0].Value<double>() - nodeWgs.lon;
                double dy = candidate[1].Value<double>() - nodeWgs.lat;
                double distance = dx * dx + dy * dy;
                if (distance < nearestDistance) { coordArray = candidate; nearestDistance = distance; }
            }
            if (coordArray == null) continue;
            usedCoordinates.Add(coordArray);

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
            data.pointClass = feature.properties?.className ?? string.Empty;
            data.pointID = feature.id ?? featureId;
            data.category = feature.properties?.category ?? "navigation";
            data.source = feature.properties?.source ?? "Ground Station";
            data.longitude = coordArray[0].Value<double>();
            data.latitude = coordArray[1].Value<double>();
            data.altitude = coordArray[2].Value<double>();
            data.confidence = feature.properties?.confidence ?? 1f;
            RefreshHeightAboveSurface(node, null, data);

            CacheLineNodeColorHex(node, resolvedLineColorHex);
            registered++;
        }

        // The repeated closing tuple is an alias of the first vertex. Keep both
        // bound so moving that vertex updates closure exactly once at release.
        if (registered > 0 && outerRing.Count > ringUsableCount)
        {
            NodeMapping first = nodeMappings.Find(m => ReferenceEquals(m.parentFeature, feature) &&
                ReferenceEquals(m.coordArray, outerRing[0]));
            if (first != null) nodeMappings.Add(new NodeMapping { node = first.node, parentFeature = feature,
                coordArray = outerRing[outerRing.Count - 1] as JArray });
        }

        if (registered > 0)
        {
            UpdateFeatureHeightAboveSurfaceData(feature);
            MarkDataDirty();
            SaveCurrentStateToPersistentStorage();
            Debug.Log($"[JsonSpawner] RegisterRuntimePolygonNodes: feature {featureId}, nodes registered={registered}");
        }

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

