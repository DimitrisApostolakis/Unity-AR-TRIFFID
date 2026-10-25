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
        HashSet<Feature> vertexHeightFeatures = new HashSet<Feature>();

        foreach (var mapping in nodeMappings)
        {
            if (mapping == null || mapping.parentFeature == null)
                continue;

            if (sourceNode != null && mapping.node != sourceNode)
                continue;

            string featureId = mapping.parentFeature.id;
            if (!string.Equals(featureId, targetId, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isPointFeature = IsPointFeature(mapping.parentFeature);
            bool storesVertexHeights = SupportsPerVertexHeight(mapping.parentFeature);

            if (!mapping.isCentroid)
                RefreshLiveHeightAboveSurface(data, mapping.node);

            if (!mapping.isCentroid && mapping.coordArray != null)
            {
                if (data.elevationUnresolved) continue;
                if (mapping.coordArray.Count >= 2)
                {
                    mapping.coordArray[0] = data.longitude;
                    mapping.coordArray[1] = data.latitude;
                }

                if (mapping.coordArray.Count >= 3)
                    mapping.coordArray[2] = data.altitude;
                else
                    mapping.coordArray.Add(data.altitude);
            }

            if (mapping.parentFeature.properties != null && !mapping.isCentroid)
            {
                mapping.parentFeature.properties.altitude_m = (float)data.altitude;

                if (isPointFeature)
                {
                    mapping.parentFeature.properties.height_above_surface_m = data.hasHeightAboveSurface
                        ? (float?)data.heightAboveSurface
                        : null;
                    StorePointHeightMetadata(mapping.parentFeature, data);
                }
                else if (storesVertexHeights)
                {
                    vertexHeightFeatures.Add(mapping.parentFeature);
                }
            }

            updated = true;
        }

        foreach (Feature feature in vertexHeightFeatures)
            UpdateFeatureHeightAboveSurfaceData(feature);

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

        SaveStageTimings timings = new SaveStageTimings();
        try
        {
            timings.world = MeasureSaveStage("AR2.Save.WorldToJson", SyncWorldToJSON);
            timings.normalize = MeasureSaveStage("AR2.Save.Normalize", NormalizeForJsonWrite);
            timings.outbox = MeasureSaveStage("AR2.Save.CaptureChanges", () => GeoJsonOutbox.CaptureChanges(currentRootData, DateTime.UtcNow, 0.5d));
            timings.labels = MeasureSaveStage("AR2.Save.StatusLabels", UpdateSyncStatusLabels);
        }
        catch (InvalidDataException ex)
        {
            ReportValidationError(ex.Message);
            return;
        }
        currentRootData.lastModified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        string json = null;
        try
        {
            timings.serialize = MeasureSaveStage("AR2.Save.Serialize", () => json = JsonConvert.SerializeObject(currentRootData, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to serialize JSON: {ex.Message}");
            return;
        }

        int saveVersion = dataVersion;
        _ = WriteJsonAsync(json, saveVersion, timings);
    }

    internal bool TryWriteExternalJsonSnapshotAtomically(string json, int expectedLocalVersion)
    {
        bool committed = false;
        double elapsed = MeasureSaveStage("AR2.Sync.Commit", () => committed = WriteExternalJsonSnapshot(json, expectedLocalVersion));
        if (logSaveStageTimings && elapsed >= 16d)
            Debug.Log($"[AR2 SyncTiming] commit={elapsed:F2}ms chars={json.Length} written={committed}");
        return committed;
    }

    private bool WriteExternalJsonSnapshot(string json, int expectedLocalVersion)
    {
        if (HasPendingLocalPersistence || dataVersion != expectedLocalVersion)
            return false;

        string targetPath = GetResolvedSaveFilePath();

        lock (fileWriteLock)
        {
            if (HasPendingLocalPersistence || dataVersion != expectedLocalVersion)
                return false;

            Root committed = JsonConvert.DeserializeObject<Root>(json);
            if (committed == null) throw new InvalidDataException("Invalid external snapshot.");
            if (!diskSaves.Write(targetPath, json, dataVersion)) return false;
            if (currentRootData != null) currentRootData.syncState = committed.syncState;
        }
        UpdateSyncStatusLabels();
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

            SyncData(movedData, movedIcon.transform);
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

    private async Task WriteJsonAsync(string json, int saveVersion, SaveStageTimings timings)
    {
        saveInProgress = true;
        bool writeSucceeded = false;

        string localSavePath = saveFilePath;
        long writeTicks = 0L, workerFinished = 0L;

        try
        {
            await Task.Run(() =>
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    lock (fileWriteLock)
                    {
                        diskSaves.Write(localSavePath, json, saveVersion);
                    }
                }
                finally
                {
                    workerFinished = Stopwatch.GetTimestamp();
                    writeTicks = workerFinished - started;
                }
            });

            double resumeMs = (Stopwatch.GetTimestamp() - workerFinished) * 1000d / Stopwatch.Frequency;
            writeSucceeded = true;
            lastSavedVersion = Math.Max(lastSavedVersion, saveVersion);
            autoSaveTimer = 0f;
            string detail = logSaveStageTimings
                ? $" | [AR2 SaveTiming] v={saveVersion} chars={json.Length} world={timings.world:F2}ms normalize={timings.normalize:F2}ms outbox={timings.outbox:F2}ms labels={timings.labels:F2}ms serialize={timings.serialize:F2}ms backgroundWrite={writeTicks * 1000d / Stopwatch.Frequency:F2}ms resumeWait={resumeMs:F2}ms followUp={saveQueued || dataVersion > lastSavedVersion}"
                : string.Empty;
            MeasureSaveStage("AR2.Save.CompletionLog", () => Debug.Log($"[JsonSpawner] Saved to: {saveFilePath} (lastModified: {currentRootData.lastModified})" + detail));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Async save failed: {ex.Message}");
        }
        finally
        {
            saveInProgress = false;
            if (writeSucceeded && (saveQueued || dataVersion > lastSavedVersion))
            {
                saveQueued = false;
                MeasureSaveStage("AR2.Save.QueuedPreparation", SaveCurrentStateToPersistentStorage);
            }
        }
    }

    private void FlushPendingSaveSynchronously()
    {
        if (currentRootData == null)
            return;

        if (dataVersion <= lastSavedVersion && !saveInProgress && !saveQueued)
            return;

        if (currentRootData.features == null ||
            (currentRootData.features.Count > 0 && !HasValidDataForSave()))
            return;

        try
        {
            SyncWorldToJSON();
            NormalizeForJsonWrite();
            GeoJsonOutbox.CaptureChanges(currentRootData, DateTime.UtcNow, 0.5d);
            UpdateSyncStatusLabels();
        }
        catch (InvalidDataException ex)
        {
            ReportValidationError(ex.Message);
            return;
        }
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
                diskSaves.Write(saveFilePath, json, dataVersion);
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
        EnsureFileWritable(targetPath);
        GeoJsonAtomicFile.Write(targetPath, content);
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
        if (pd == null || pd.elevationUnresolved) return;
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
                        f["properties"]["height_above_surface_m"] = pd.hasHeightAboveSurface ? new JValue(pd.heightAboveSurface) : JValue.CreateNull();
                        f["properties"]["heightSource"] = pd.heightSource;
                        f["properties"]["heightStatus"] = pd.heightStatus;
                        break;
                    }
                }
                GeoJsonAtomicFile.Write(changesPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
                Debug.Log($"[JsonSpawner] Live Move Saved for New Object ID: {pd.pointID}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[JsonSpawner] Failed to update coordinates_changes.json on move: {ex.Message}");
        }
    }
}
