using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(JsonSpawner))]
public class GeoJsonSyncCoordinator : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Local GeoJSON runtime owner used for path resolution, spawning, and guarded file commits.")]
    [SerializeField] private JsonSpawner jsonSpawner;
    [Tooltip("REST client used for feature, telemetry, and observer-status requests.")]
    [SerializeField] private GeoJsonApiManager apiManager;

    [Header("Startup")]
    [Tooltip("Requests an initial remote refresh whenever this component becomes enabled.")]
    [SerializeField] private bool loadFromServerOnEnable = true;

    [Header("Local Change Tracking")]
    [Min(0f)]
    [Tooltip("Seconds between checks of the shared local GeoJSON file for external or local changes.")]
    [SerializeField] private float localScanIntervalSeconds = 0.25f;
    [Min(0f)]
    [Tooltip("Delay used to combine repeated local updates for the same feature before sending REST work.")]
    [SerializeField] private float updateDebounceSeconds = 0.5f;

    [Header("REST Retry Queue")]
    [Min(0)]
    [Tooltip("Maximum number of retry attempts after a local REST synchronization operation fails.")]
    [SerializeField] private int maxRetries = 6;
    [Min(0f)]
    [Tooltip("Initial delay in seconds used by exponential REST retry backoff.")]
    [SerializeField] private float retryBaseDelaySeconds = 0.75f;
    [Min(0f)]
    [Tooltip("Maximum delay in seconds allowed between REST retry attempts.")]
    [SerializeField] private float retryMaxDelaySeconds = 20f;

    [Header("Remote Polling")]
    [Tooltip("Periodically checks for remote GeoJSON changes.")]
    [SerializeField] private bool enableRemotePolling = true;
    [Min(0f)]
    [Tooltip("Seconds between remote change checks while remote polling is enabled.")]
    [SerializeField] private float remotePollingIntervalSeconds = 30f;

    [Header("Latest MQTT Polling")]
    [Tooltip("Periodically fetches the latest MQTT telemetry marker from the API.")]
    [SerializeField] private bool enableLatestMqttPolling = true;
    [Min(0f)]
    [Tooltip("Seconds between latest-MQTT telemetry requests.")]
    [SerializeField] private float latestMqttPollingIntervalSeconds = 4f;

    [Header("Debug Logging")]
    [Tooltip("Emits detailed synchronization, polling, and retry diagnostics.")]
    [SerializeField] private bool verboseLogs = true;

    [Header("API Timing")]
    [Min(0f)]
    [Tooltip("Maximum seconds to wait for an API callback before treating the operation as timed out.")]
    [SerializeField] private float apiCallbackTimeoutSeconds = 20f;

    [Header("Observer Status On Remote Refresh")]
    [Tooltip("Updates the observer-status endpoint after a remote snapshot is successfully applied.")]
    [SerializeField] private bool resetArUpdatedAfterRemoteRefresh = true;
    [Tooltip("Observer-status field patched after a successful remote refresh.")]
    [SerializeField] private string remoteRefreshStatusField = "ar_updated";
    [Tooltip("Value written to the configured observer-status field after a successful remote refresh.")]
    [SerializeField] private int remoteRefreshStatusValue = 0;

    [Header("Refresh Safety")]
    [Min(0f)]
    [Tooltip("Grace period in seconds during which normal remote refresh waits after a local change.")]
    [SerializeField] private float localChangeGraceSeconds = 1.5f;

    [Header("Conflict Policy")]
    [Tooltip("Uses observer-status flags to avoid fetching when no external update has been reported. Successful complete snapshots apply server changes and deletions; failed requests retain the local backup.")]
    [SerializeField] private bool fetchOnlyWhenExternalUpdates = true;
    [Tooltip("Allows a remote fetch when the observer-status endpoint cannot be reached.")]
    [SerializeField] private bool fallbackToFetchWhenStatusUnavailable = true;
    [Tooltip("Removes duplicate features from a fetched snapshot before it is applied locally.")]
    [SerializeField] private bool deduplicateFetchedFeatures = true;


    [Header("Conflict resolution")]
    [Tooltip("Feature ID used by the Keep Local, Accept Server, and Restore Deletion context-menu actions.")]
    [SerializeField] private string resolutionFeatureId;

    [Header("Runtime JSON Diagnostics")]
    [SerializeField] private int localSnapshotReadCount;
    [SerializeField] private int localSnapshotWriteCount;
    public int LocalSnapshotReadCount => localSnapshotReadCount;
    public int LocalSnapshotWriteCount => localSnapshotWriteCount;

    private bool localSnapshotObserved;
    private string observedPath;
    private bool observedFileExists;
    private long observedFileLength;
    private DateTime observedFileWriteUtc;
    private int observedPersistenceVersion;
    private DateTime nextPendingOperationUtc = DateTime.MinValue;
    private bool observedReconciliationNeeded;

    private bool refreshRequested;
    private bool remoteSnapshotRequired;
    private bool restorePendingState;
    private DateTime nextRemoteCheck;
    private string saveFileAbsolutePath;
    private int lastObservedLocalVersion = -1;
    private DateTime lastLocalChangeUtc = DateTime.MinValue;
    public string LastStatus { get; private set; }
    public event Action<string> OnSyncStatus;

    private sealed class Response
    {
        public bool completed;
        public bool ok;
        public string message;
        public string remoteId;
        public Root root;
        public MqttLatestResponse latest;
        public bool hasExternalUpdates;
    }

    private void Awake()
    {
        if (jsonSpawner == null) jsonSpawner = GetComponent<JsonSpawner>();
        if (apiManager == null) apiManager = GetComponent<GeoJsonApiManager>();
        saveFileAbsolutePath = jsonSpawner == null ? null : jsonSpawner.GetResolvedSaveFilePath();
    }

    private void OnEnable()
    {
        Awake();
        restorePendingState = true;
        refreshRequested = loadFromServerOnEnable;
        nextRemoteCheck = DateTime.UtcNow;
        StartCoroutine(Synchronize());
        if (enableLatestMqttPolling) StartCoroutine(LatestMqttLoop());
    }

    private void OnDisable()
    {
        // In-flight state is already durable. A stopped create is reconciled on
        // the next enable instead of being submitted a second time blindly.
        StopAllCoroutines();
    }

    [ContextMenu("Sync/Initial Load From Server")]
    public void LoadFromServer() { RefreshFromServer(); }

    [ContextMenu("Sync/Refresh From Server")]
    public void RefreshFromServer()
    {
        refreshRequested = true;
        Debug.Log("[GeoJsonSyncCoordinator] Remote refresh requested.");
        if (!isActiveAndEnabled)
            Debug.LogWarning("[GeoJsonSyncCoordinator] Refresh cannot run while the component or its GameObject is disabled.");
    }

    [ContextMenu("Sync/Retry Pending Edits")]
    public void RetryPendingEdits()
    {
        MutateLocal(root => GeoJsonOutbox.Retry(root), false);
        refreshRequested = true;
    }

    [ContextMenu("Sync/Resolve Selected Feature - Keep Local")]
    public void KeepLocalVersion() { ResolveConflict(resolutionFeatureId, true); }

    [ContextMenu("Sync/Resolve Selected Feature - Accept Server")]
    public void AcceptServerVersion() { ResolveConflict(resolutionFeatureId, false); }

    [ContextMenu("Sync/Restore Pending Deletion")]
    public void RestorePendingDeletion()
    {
        MutateLocal(root => GeoJsonOutbox.RestoreDeletion(root, resolutionFeatureId), true);
    }

    public void ResolveConflict(string featureId, bool keepLocal)
    {
        if (string.IsNullOrWhiteSpace(featureId)) { NotifyStatus("Select a feature ID for conflict resolution."); return; }
        // Explicit Keep Local can recreate an unconfirmed create. The UI labels
        // this state separately because servers without idempotency cannot offer
        // an exactly-once guarantee after an ambiguous request outcome.
        MutateLocal(root => GeoJsonOutbox.Resolve(root, featureId, keepLocal), true);
        refreshRequested = true;
    }

    private void MutateLocal(Action<Root> mutation, bool respawn)
    {
        if (IsLocalBusy()) { NotifyStatus("Finish the current move or local save before resolving synchronization."); return; }
        Root root;
        if (!Read(out root)) return;
        int version = jsonSpawner.LocalPersistenceVersion;
        mutation(root);
        Commit(root, version, respawn);
    }

    private bool IsLocalBusy()
    {
        return jsonSpawner == null || jsonSpawner.HasPendingLocalPersistence ||
            jsonSpawner.HasActiveAnnotationDraft || HasActiveManipulation();
    }

    private IEnumerator Synchronize()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, localScanIntervalSeconds));
            if (apiManager == null || IsLocalBusy()) continue;
            if (lastObservedLocalVersion != jsonSpawner.LocalPersistenceVersion)
            {
                lastObservedLocalVersion = jsonSpawner.LocalPersistenceVersion;
                lastLocalChangeUtc = DateTime.UtcNow;
            }
            if (!NeedsLocalInspection()) continue;
            Root local;
            if (!Read(out local)) continue;
            // A durable snapshot can already match the server while a fresh
            // scene has no feature objects. Restore the backup independently
            // of whether a later GET changes its content, also while offline.
            if (!jsonSpawner.HasLoadedFeatureCollection && File.Exists(saveFileAbsolutePath))
            {
                jsonSpawner.SpawnFromJson();
                if (IsLocalBusy()) continue;
            }
            int version = jsonSpawner.LocalPersistenceVersion;
            string before = JsonConvert.SerializeObject(local);
            if (restorePendingState) GeoJsonOutbox.RestoreAfterRestart(local);
            GeoJsonOutbox.CaptureChanges(local, DateTime.UtcNow, updateDebounceSeconds);
            if (before != JsonConvert.SerializeObject(local) && !Commit(local, version, false)) continue;
            restorePendingState = false;

            GeoJsonPendingEdit candidate = GeoJsonOutbox.Next(local, DateTime.UtcNow);
            bool reconciliationNeeded = local.syncState.pending.Any(p => p.status == "reconcile");
            bool checkRemote = refreshRequested || candidate != null ||
                (enableRemotePolling && DateTime.UtcNow >= nextRemoteCheck) ||
                ((reconciliationNeeded || remoteSnapshotRequired) && DateTime.UtcNow >= nextRemoteCheck);
            if (!checkRemote) { ReportPending(local); continue; }
            if (candidate == null && !refreshRequested && !reconciliationNeeded && !remoteSnapshotRequired)
            {
                if ((DateTime.UtcNow - lastLocalChangeUtc).TotalSeconds < Math.Max(0f, localChangeGraceSeconds)) continue;
                if (fetchOnlyWhenExternalUpdates)
                {
                    Response status = new Response();
                    apiManager.GetCurrentStatus((ok, message, value) =>
                    {
                        status.ok = ok; status.message = message;
                        status.hasExternalUpdates = value != null && (value.fe_updated > 0 || value.mobile_updated > 0);
                        status.completed = true;
                    });
                    yield return Wait(status, null);
                    if ((status.ok && !status.hasExternalUpdates) || (!status.ok && !fallbackToFetchWhenStatusUnavailable))
                    {
                        nextRemoteCheck = DateTime.UtcNow.AddSeconds(Mathf.Max(2f, remotePollingIntervalSeconds));
                        continue;
                    }
                }
            }

            // Always compare with the server before a write. This detects an
            // observed conflicting edit; atomic concurrent-write protection also
            // needs conditional version support from the server.
            version = jsonSpawner.LocalPersistenceVersion;
            Response fetch = new Response();
            yield return Fetch(fetch);
            nextRemoteCheck = DateTime.UtcNow.AddSeconds(Mathf.Max(2f, remotePollingIntervalSeconds));
            refreshRequested = false;
            if (!fetch.ok || fetch.root == null)
            {
                remoteSnapshotRequired = true;
                NotifyStatus("Server refresh failed. Local backup retained. " + fetch.message);
                // Do not mark an operation as sent when the preliminary GET
                // could not connect. Offline creates remain safely retryable.
                yield return new WaitForSecondsRealtime(Mathf.Max(0.75f, retryBaseDelaySeconds));
                continue;
            }
            if (IsLocalBusy() || version != jsonSpawner.LocalPersistenceVersion)
            { refreshRequested = true; continue; }
            if (!Read(out local)) continue;
            string originalFeatures = JsonConvert.SerializeObject(local.features);
            HashSet<string> previousIds = new HashSet<string>(local.features.Where(f => f != null).Select(f => f.id), StringComparer.OrdinalIgnoreCase);
            bool merged = false;
            string mergeError = null;
            try
            {
                if (deduplicateFetchedFeatures) RemoveIdenticalDuplicates(fetch.root);
                merged = GeoJsonOutbox.MergeRemote(local, fetch.root);
            }
            catch (Exception ex) { mergeError = ex.Message; }
            if (mergeError != null)
            {
                remoteSnapshotRequired = true;
                NotifyStatus("Server comparison failed. Local backup retained: " + mergeError);
                continue;
            }
            bool contentChanged = originalFeatures != JsonConvert.SerializeObject(local.features);
            bool initializeScene = !jsonSpawner.HasLoadedFeatureCollection;
            if ((merged || initializeScene) && !Commit(local, version, contentChanged || initializeScene))
            { remoteSnapshotRequired = true; continue; }
            remoteSnapshotRequired = false;
            if (verboseLogs)
            {
                previousIds.ExceptWith(local.features.Where(f => f != null).Select(f => f.id));
                Debug.Log("[GeoJsonSyncCoordinator] Remote refresh applied: server=" + fetch.root.features.Count +
                    ", local=" + local.features.Count + ", removed=" + previousIds.Count +
                    ", pending=" + local.syncState.pending.Count + ". Server changes and deletions take priority.");
            }
            if (contentChanged && resetArUpdatedAfterRemoteRefresh) yield return NotifyServerOfRemoteRefresh();
            if (contentChanged || IsLocalBusy()) continue;

            if (!Read(out local)) continue;
            candidate = GeoJsonOutbox.Next(local, DateTime.UtcNow);
            if (candidate == null) { ReportPending(local); continue; }
            if (candidate.kind != "delete" && !ValidForSend(candidate.payload))
            {
                candidate.error = "Invalid or unresolved geographic coordinates. Correct the feature before synchronization.";
                GeoJsonPendingEdit invalid = GeoJsonOutbox.Find(local, candidate.featureId);
                invalid.status = "conflict";
                invalid.error = candidate.error;
                Commit(local, jsonSpawner.LocalPersistenceVersion, false);
                NotifyStatus(candidate.error);
                continue;
            }
            version = jsonSpawner.LocalPersistenceVersion;
            GeoJsonOutbox.MarkDispatched(local, candidate);
            if (!Commit(local, version, false)) continue;
            Feature payload = GeoJsonOutbox.NetworkPayload(local, candidate);
            string remoteId;
            local.syncState.remoteIds.TryGetValue(candidate.featureId, out remoteId);
            Response write = new Response();
            yield return Send(candidate, payload, remoteId, write);

            // A newer local edit may have been saved during the request. Reload
            // it and acknowledge only the immutable revision that was dispatched.
            bool recorded = false;
            while (!recorded && isActiveAndEnabled)
            {
                if (IsLocalBusy()) { yield return null; continue; }
                if (!Read(out local)) break;
                version = jsonSpawner.LocalPersistenceVersion;
                if (write.ok) GeoJsonOutbox.Acknowledge(local, candidate, write.remoteId ?? remoteId);
                else GeoJsonOutbox.Fail(local, candidate, write.message, maxRetries, retryBaseDelaySeconds, retryMaxDelaySeconds, DateTime.UtcNow);
                recorded = Commit(local, version, false);
                if (!recorded) yield return new WaitForSecondsRealtime(0.25f);
            }
            if (!recorded) continue;
            ReportPending(local);
            if (write.ok) yield return NotifyServerOfLocalChange();
            else if (candidate.kind == "create") refreshRequested = true;
        }
    }

    private bool NeedsLocalInspection()
    {
        if (!localSnapshotObserved || restorePendingState || refreshRequested) return true;
        DateTime now = DateTime.UtcNow;
        if (now >= nextPendingOperationUtc ||
            ((enableRemotePolling || observedReconciliationNeeded || remoteSnapshotRequired) && now >= nextRemoteCheck)) return true;
        if (observedPersistenceVersion != jsonSpawner.LocalPersistenceVersion) return true;
        try
        {
            string path = jsonSpawner.GetResolvedSaveFilePath();
            if (!string.Equals(path, observedPath, StringComparison.Ordinal)) return true;
            FileInfo file = new FileInfo(path);
            bool exists = file.Exists;
            return exists != observedFileExists ||
                (exists && (file.Length != observedFileLength || file.LastWriteTimeUtc != observedFileWriteUtc));
        }
        catch { return true; } // Read reports errors and retains the pending data.
    }

    private void ObserveLocalSnapshot(Root root, string path, bool exists, long length, DateTime writeUtc, int version)
    {
        observedPath = path;
        observedFileExists = exists;
        observedFileLength = length;
        observedFileWriteUtc = writeUtc;
        observedPersistenceVersion = version;
        nextPendingOperationUtc = DateTime.MaxValue;
        observedReconciliationNeeded = false;
        foreach (GeoJsonPendingEdit edit in root.syncState.pending)
        {
            if (edit.status == "reconcile") observedReconciliationNeeded = true;
            if (edit.status == "conflict" || edit.status == "reconcile" || edit.status == "inflight" ||
                (edit.kind == "delete" && !root.syncState.remoteIds.ContainsKey(edit.featureId))) continue;
            long ticks = Math.Min(DateTime.MaxValue.Ticks, Math.Max(0L, edit.notBeforeUtcTicks));
            DateTime due = new DateTime(ticks, DateTimeKind.Utc);
            if (due < nextPendingOperationUtc) nextPendingOperationUtc = due;
        }
        localSnapshotObserved = true;
    }

    private bool Read(out Root root)
    {
        root = null;
        try
        {
            saveFileAbsolutePath = jsonSpawner.GetResolvedSaveFilePath();
            // Observe metadata before reading. An external replacement during
            // the read will then be detected on the next inspection.
            FileInfo file = new FileInfo(saveFileAbsolutePath);
            bool exists = file.Exists;
            long length = exists ? file.Length : 0L;
            DateTime writeUtc = exists ? file.LastWriteTimeUtc : DateTime.MinValue;
            int version = jsonSpawner.LocalPersistenceVersion;
            if (!exists)
            {
                root = new Root { features = new List<Feature>() };
                GeoJsonOutbox.Initialize(root, false);
            }
            else
            {
                root = JsonConvert.DeserializeObject<Root>(File.ReadAllText(saveFileAbsolutePath));
                localSnapshotReadCount++;
                if (root == null) throw new InvalidDataException("Local GeoJSON is empty.");
                GeoJsonOutbox.Initialize(root, true);
            }
            ObserveLocalSnapshot(root, saveFileAbsolutePath, exists, length, writeUtc, version);
            return true;
        }
        catch (Exception ex) { NotifyStatus("Cannot read local edits: " + ex.Message); return false; }
    }

    private bool Commit(Root root, int expectedVersion, bool respawn)
    {
        try
        {
            if (IsLocalBusy()) return false;
            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            if (!jsonSpawner.TryWriteExternalJsonSnapshotAtomically(json, expectedVersion)) return false;
            localSnapshotWriteCount++;
            // Never reuse a mutated in-memory snapshot after committing. Read
            // the durable state once, then use cheap metadata checks while idle.
            localSnapshotObserved = false;
            if (respawn) jsonSpawner.SpawnFromJson();
            return true;
        }
        catch (Exception ex) { NotifyStatus("Local save failed. Pending edits retained: " + ex.Message); return false; }
    }

    private IEnumerator Fetch(Response result)
    {
        apiManager.FetchAllFeatures((ok, message, root) =>
        { result.ok = ok; result.message = message; result.root = root; result.completed = true; });
        yield return Wait(result, null);
    }

    private IEnumerator Send(GeoJsonPendingEdit edit, Feature payload, string remoteId, Response result)
    {
        if (edit.kind == "create")
            apiManager.CreateFeature(payload, (ok, id, raw) =>
            {
                result.ok = ok && !string.IsNullOrWhiteSpace(id);
                result.remoteId = result.ok ? id : null;
                result.message = result.ok ? "OK" : (ok ? "Server did not confirm a feature ID." : id);
                result.completed = true;
            }, edit.operationId);
        else if (edit.kind == "update")
            apiManager.UpdateFeature(remoteId, payload, (ok, message) =>
            { result.ok = ok; result.message = message; result.completed = true; }, edit.operationId);
        else if (edit.kind == "delete")
            apiManager.DeleteFeature(remoteId, (ok, message) =>
            { result.ok = ok; result.message = message; result.completed = true; }, edit.operationId);
        else { result.completed = true; result.message = "Operation requires reconciliation."; }
        yield return Wait(result, edit.operationId);
    }

    private IEnumerator Wait(Response result, string operationId)
    {
        float started = Time.realtimeSinceStartup;
        while (!result.completed && Time.realtimeSinceStartup - started < Mathf.Max(1f, apiCallbackTimeoutSeconds)) yield return null;
        if (!result.completed)
        {
            if (operationId != null) apiManager.AbortOperation(operationId);
            result.ok = false;
            result.message = "Request timed out. Outcome needs verification.";
            result.completed = true;
        }
    }

    private IEnumerator LatestMqttLoop()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(1f, latestMqttPollingIntervalSeconds));
            if (apiManager == null || jsonSpawner == null) continue;
            Response result = new Response();
            apiManager.FetchLatestMqttStatus((ok, message, latest) =>
            { result.ok = ok; result.message = message; result.latest = latest; result.completed = true; });
            yield return Wait(result, null);
            if (result.ok && result.latest != null) jsonSpawner.SpawnLatestMqttIcon(result.latest);
        }
    }

    private IEnumerator NotifyServerOfLocalChange()
    {
        Response result = new Response();
        apiManager.PatchCurrentStatusFlags(0, 0, 1, (ok, message, ignored) =>
        { result.ok = ok; result.message = message; result.completed = true; });
        yield return Wait(result, null);
        if (!result.ok && verboseLogs) Debug.LogWarning("[GeoJsonSyncCoordinator] Feature delivered, observer-status notification failed: " + result.message);
    }

    private IEnumerator NotifyServerOfRemoteRefresh()
    {
        Response result = new Response();
        apiManager.PatchCurrentStatusField(remoteRefreshStatusField, remoteRefreshStatusValue, (ok, message, ignored) =>
        { result.ok = ok; result.message = message; result.completed = true; });
        yield return Wait(result, null);
        if (!result.ok && verboseLogs) Debug.LogWarning("[GeoJsonSyncCoordinator] Remote state applied, status notification failed: " + result.message);
    }

    private static void RemoveIdenticalDuplicates(Root root)
    {
        if (root == null || root.features == null) return;
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        root.features = root.features.Where(feature => feature == null || string.IsNullOrWhiteSpace(feature.id) ||
            seen.Add(feature.id + ":" + GeoJsonOutbox.Fingerprint(feature))).ToList();
        // Divergent records with the same ID remain and are rejected by MergeRemote.
    }

    private static bool ValidForSend(Feature feature)
    {
        if (feature == null || feature.geometry == null || string.IsNullOrWhiteSpace(feature.id)) return false;
        // GeoJSON permits two-dimensional positions. Missing elevation is not
        // replaced with a fabricated altitude or treated as missing longitude/latitude.
        if (feature.properties != null && feature.properties.elevationStatus == "unresolved" &&
            feature.geometry.type == "Point" && feature.geometry.coordinates is JArray &&
            ((JArray)feature.geometry.coordinates).Count > 2) return false;
        string error;
        // Validation must not alter the dispatch payload: acknowledgement hashes
        // must describe the same geographic data that was saved locally.
        if (!GeoJsonPolygonValidator.TryNormalizeFeature(GeoJsonOutbox.Clone(feature), out error)) return false;
        return CoordinatesAreFinite(feature.geometry);
    }

    private static bool CoordinatesAreFinite(Geometry geometry)
    {
        if (geometry.type == "GeometryCollection")
            return geometry.geometries != null && geometry.geometries.Count > 0 && geometry.geometries.All(CoordinatesAreFinite);
        return FiniteTree(geometry.coordinates);
    }

    private static bool FiniteTree(JToken token)
    {
        JArray array = token as JArray;
        if (array == null || array.Count == 0) return false;
        if (array[0] is JArray) return array.All(FiniteTree);
        if (array.Count < 2) return false;
        double lon, lat;
        if (!GeoJsonPolygonValidator.TryNumber(array[0], out lon) || !GeoJsonPolygonValidator.TryNumber(array[1], out lat) ||
            lon < -180d || lon > 180d || lat < -90d || lat > 90d) return false;
        for (int i = 2; i < array.Count; i++) { double value; if (!GeoJsonPolygonValidator.TryNumber(array[i], out value)) return false; }
        return true;
    }

    private bool HasActiveManipulation()
    {
        // Even forced refresh must not replace an actively dragged marker.
        FloatingIcon[] icons = FindObjectsByType<FloatingIcon>(FindObjectsSortMode.None);
        return icons != null && icons.Any(icon => icon != null && icon.IsBeingManipulated);
    }

    private void ReportPending(Root root)
    {
        int attention = root.syncState.pending.Count(p => p.status == "failed" || p.status == "conflict" || p.status == "reconcile");
        NotifyStatus(root.syncState.pending.Count == 0 ? "Synchronization completed" :
            root.syncState.pending.Count + " pending edit(s), " + attention + " need attention.");
    }

    private void NotifyStatus(string status)
    {
        if (LastStatus == status) return;
        LastStatus = status;
        if (verboseLogs) Debug.Log("[GeoJsonSyncCoordinator] " + status);
        OnSyncStatus?.Invoke(status);
    }
}

