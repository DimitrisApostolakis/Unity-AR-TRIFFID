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
    private enum OpType
    {
        Create,
        Update,
        Delete
    }

    private enum LocalWriteResult
    {
        Success,
        LocalStateChanged,
        Failed
    }

    private sealed class PendingOp
    {
        public OpType Type;
        public string Id;
        public string ConfirmedRemoteId;
        public int Attempt;
        public DateTime NotBeforeUtc;
    }

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
    [Tooltip("Defers remote refresh while a marker is actively being manipulated.")]
    [SerializeField] private bool skipRefreshWhileManipulating = true;
    [Min(0f)]
    [Tooltip("Grace period in seconds during which normal remote refresh waits after a local change.")]
    [SerializeField] private float localChangeGraceSeconds = 1.5f;

    [Header("Conflict Policy")]
    [Tooltip("Uses observer-status flags to avoid fetching when no external update has been reported.")]
    [SerializeField] private bool fetchOnlyWhenExternalUpdates = true;
    [Tooltip("Allows a remote fetch when the observer-status endpoint cannot be reached.")]
    [SerializeField] private bool fallbackToFetchWhenStatusUnavailable = true;
    [Tooltip("Removes duplicate features from a fetched snapshot before it is applied locally.")]
    [SerializeField] private bool deduplicateFetchedFeatures = true;

    private readonly Queue<PendingOp> queue = new Queue<PendingOp>();
    private readonly Dictionary<string, int> updateDebounceTokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> idMapLocalToRemote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private bool queueRunning;
    private bool suppressLocalWatcher;
    private Coroutine localWatcherRoutine;
    private Coroutine remotePollingRoutine;
    private Coroutine latestMqttPollingRoutine;

    private string saveFileAbsolutePath;
    private Root lastLocalRoot;
    private string lastLocalJsonHash = string.Empty;
    private string lastRemoteJsonHash = string.Empty;
    private DateTime lastLocalChangeUtc = DateTime.MinValue;
    private bool remoteRefreshInProgress;
    private bool queuedRefresh;
    private bool queuedForceRefresh;
    private Coroutine deferredRefreshRoutine;
    private bool hasUnresolvedLocalSyncFailure;

    public event Action<string> OnSyncStatus;

    private void Awake()
    {
        if (jsonSpawner == null)
            jsonSpawner = GetComponent<JsonSpawner>();

        if (apiManager == null)
            apiManager = GetComponent<GeoJsonApiManager>();

        RefreshSaveFileAbsolutePath();
        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (jsonSpawner == null)
        {
            Debug.LogError("[GeoJsonSyncCoordinator] JsonSpawner is missing; local synchronization cannot run.", this);
        }
        else if (string.IsNullOrWhiteSpace(saveFileAbsolutePath))
        {
            Debug.LogError("[GeoJsonSyncCoordinator] Shared GeoJSON save path is empty.", this);
        }

        if (apiManager == null)
            Debug.LogError("[GeoJsonSyncCoordinator] GeoJsonApiManager is missing; remote synchronization cannot run.", this);

        if (!IsFinite(localScanIntervalSeconds) || localScanIntervalSeconds <= 0f)
            Debug.LogWarning("[GeoJsonSyncCoordinator] localScanIntervalSeconds must be positive.", this);

        if (!IsFinite(updateDebounceSeconds) || updateDebounceSeconds < 0f)
            Debug.LogWarning("[GeoJsonSyncCoordinator] updateDebounceSeconds must be finite and non-negative.", this);

        if (maxRetries < 0)
            Debug.LogWarning("[GeoJsonSyncCoordinator] maxRetries must be non-negative.", this);

        if (!IsFinite(retryBaseDelaySeconds) || retryBaseDelaySeconds < 0f)
            Debug.LogWarning("[GeoJsonSyncCoordinator] retryBaseDelaySeconds must be finite and non-negative.", this);

        if (!IsFinite(retryMaxDelaySeconds) || retryMaxDelaySeconds < 0f)
            Debug.LogWarning("[GeoJsonSyncCoordinator] retryMaxDelaySeconds must be finite and non-negative.", this);

        if (enableRemotePolling && (!IsFinite(remotePollingIntervalSeconds) || remotePollingIntervalSeconds <= 0f))
            Debug.LogWarning("[GeoJsonSyncCoordinator] remotePollingIntervalSeconds must be positive when remote polling is enabled.", this);

        if (enableLatestMqttPolling && (!IsFinite(latestMqttPollingIntervalSeconds) || latestMqttPollingIntervalSeconds <= 0f))
            Debug.LogWarning("[GeoJsonSyncCoordinator] latestMqttPollingIntervalSeconds must be positive when MQTT polling is enabled.", this);

        if (!IsFinite(apiCallbackTimeoutSeconds) || apiCallbackTimeoutSeconds <= 0f)
            Debug.LogWarning("[GeoJsonSyncCoordinator] apiCallbackTimeoutSeconds must be positive.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnEnable()
    {
        RefreshSaveFileAbsolutePath();

        if (!string.IsNullOrWhiteSpace(saveFileAbsolutePath) && File.Exists(saveFileAbsolutePath))
            TryLoadLocalSnapshot(out lastLocalRoot, out lastLocalJsonHash);

        lastLocalChangeUtc = DateTime.UtcNow;

        localWatcherRoutine = StartCoroutine(LocalFileWatcherLoop());

        if (enableRemotePolling)
            remotePollingRoutine = StartCoroutine(RemotePollingLoop());

        if (enableLatestMqttPolling)
            latestMqttPollingRoutine = StartCoroutine(LatestMqttPollingLoop());

        if (loadFromServerOnEnable)
            StartCoroutine(InitialLoadFromServer());

        if (queuedRefresh && deferredRefreshRoutine == null)
            deferredRefreshRoutine = StartCoroutine(DeferredRefreshLoop());
    }

    private void RefreshSaveFileAbsolutePath()
    {
        if (jsonSpawner == null)
            jsonSpawner = GetComponent<JsonSpawner>();

        saveFileAbsolutePath = jsonSpawner != null
            ? jsonSpawner.GetResolvedSaveFilePath()
            : string.Empty;
    }

    private void OnDisable()
    {
        if (localWatcherRoutine != null)
            StopCoroutine(localWatcherRoutine);

        if (remotePollingRoutine != null)
            StopCoroutine(remotePollingRoutine);

        if (latestMqttPollingRoutine != null)
            StopCoroutine(latestMqttPollingRoutine);

        if (deferredRefreshRoutine != null)
        {
            StopCoroutine(deferredRefreshRoutine);
            deferredRefreshRoutine = null;
        }
    }

    [ContextMenu("Sync/Initial Load From Server")]
    public void LoadFromServer()
    {
        StartCoroutine(InitialLoadFromServer());
    }

    [ContextMenu("Sync/Refresh From Server")]
    public void RefreshFromServer()
    {
        StartCoroutine(RefreshRemote(forceApply: true));
    }

    private IEnumerator InitialLoadFromServer()
    {
        yield return RefreshRemote(forceApply: true);
    }

    private IEnumerator LocalFileWatcherLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(0.05f, localScanIntervalSeconds));

            if (suppressLocalWatcher)
                continue;

            if (string.IsNullOrWhiteSpace(saveFileAbsolutePath) || !File.Exists(saveFileAbsolutePath))
                continue;

            if (!TryReadLocalJson(out string currentJson, out string currentHash))
                continue;

            if (string.Equals(currentHash, lastLocalJsonHash, StringComparison.Ordinal))
                continue;

            if (!TryDeserializeLocalSnapshot(currentJson, out Root currentRoot))
                continue;

            if (lastLocalRoot != null)
                DetectAndQueueLocalChanges(lastLocalRoot, currentRoot);

            lastLocalRoot = currentRoot;
            lastLocalJsonHash = currentHash;
            MarkLocalChangeObserved();

            if (!queueRunning && queue.Count > 0)
                StartCoroutine(ProcessQueue());
        }
    }

    private void DetectAndQueueLocalChanges(Root oldRoot, Root newRoot)
    {
        Dictionary<string, Feature> oldById = BuildFeatureMap(oldRoot);
        Dictionary<string, Feature> newById = BuildFeatureMap(newRoot);

        foreach (KeyValuePair<string, Feature> kv in newById)
        {
            string id = kv.Key;
            Feature next = kv.Value;

            if (!oldById.TryGetValue(id, out Feature prev))
            {
                QueueOp(OpType.Create, id);
                continue;
            }

            if (!AreFeaturesEquivalent(prev, next))
                DebounceUpdate(id);
        }

        foreach (KeyValuePair<string, Feature> kv in oldById)
        {
            if (!newById.ContainsKey(kv.Key))
                QueueOp(OpType.Delete, kv.Key);
        }
    }

    private void QueueOp(OpType type, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        queue.Enqueue(new PendingOp
        {
            Type = type,
            Id = id,
            Attempt = 0,
            NotBeforeUtc = DateTime.UtcNow
        });
    }

    private void DebounceUpdate(string id)
    {
        int token = 1;
        if (updateDebounceTokens.TryGetValue(id, out int existing))
            token = existing + 1;

        updateDebounceTokens[id] = token;
        StartCoroutine(DebouncedUpdate(id, token));
    }

    private IEnumerator DebouncedUpdate(string id, int token)
    {
        yield return new WaitForSeconds(Mathf.Max(0.05f, updateDebounceSeconds));

        if (!updateDebounceTokens.TryGetValue(id, out int current) || current != token)
            yield break;

        updateDebounceTokens.Remove(id);
        QueueOp(OpType.Update, id);

        if (!queueRunning)
            StartCoroutine(ProcessQueue());
    }

    private IEnumerator ProcessQueue()
    {
        queueRunning = true;

        while (queue.Count > 0)
        {
            PendingOp op = queue.Dequeue();

            TimeSpan wait = op.NotBeforeUtc - DateTime.UtcNow;
            if (wait.TotalMilliseconds > 1)
                yield return new WaitForSeconds((float)wait.TotalSeconds);

            bool done = false;
            bool ok = false;
            string msg = string.Empty;

            yield return ExecuteOp(op, (success, message) =>
            {
                done = true;
                ok = success;
                msg = message;
            });

            if (!done || !ok)
            {
                Retry(op, done ? msg : "operation not completed");
                continue;
            }

            if (verboseLogs)
                Debug.Log($"[GeoJsonSyncCoordinator] Synced {op.Type} id={op.Id}");
        }

        queueRunning = false;
    }

    private void Retry(PendingOp op, string reason)
    {
        op.Attempt++;

        if (op.Attempt <= Mathf.Max(0, maxRetries))
        {
            float delay = Mathf.Min(
                Mathf.Max(0.1f, retryBaseDelaySeconds) * Mathf.Pow(2f, op.Attempt - 1),
                Mathf.Max(retryMaxDelaySeconds, retryBaseDelaySeconds)
            );

            op.NotBeforeUtc = DateTime.UtcNow.AddSeconds(delay);
            queue.Enqueue(op);

            if (!queueRunning)
                StartCoroutine(ProcessQueue());

            if (verboseLogs)
                Debug.LogWarning($"[GeoJsonSyncCoordinator] Retry #{op.Attempt} for {op.Type} id={op.Id} in {delay:F2}s. Reason: {reason}");

            return;
        }

        hasUnresolvedLocalSyncFailure = true;
        NotifyStatus("Synchronization failed");
        Debug.LogWarning($"[GeoJsonSyncCoordinator] Dropped {op.Type} id={op.Id} after retries. Reason: {reason}");
    }

    private IEnumerator ExecuteOp(PendingOp op, Action<bool, string> callback)
    {
        if (apiManager == null || jsonSpawner == null)
        {
            callback?.Invoke(false, "Missing references");
            yield break;
        }

        if (!TryLoadLocalSnapshot(out Root localRoot, out _))
        {
            callback?.Invoke(false, "Cannot read local JSON");
            yield break;
        }

        Dictionary<string, Feature> byId = BuildFeatureMap(localRoot);

        if (op.Type == OpType.Delete)
        {
            string remoteId = ResolveRemoteId(op.Id);
            if (string.IsNullOrWhiteSpace(remoteId))
                remoteId = op.Id;

            bool deleteOk = false;
            string deleteMsg = string.Empty;
            yield return ApiDelete(remoteId, (ok, msg) =>
            {
                deleteOk = ok;
                deleteMsg = msg;
            });

            if (deleteOk)
            {
                UnmapId(op.Id);
                yield return NotifyStatusEndpointForLocalChange();
            }

            callback?.Invoke(deleteOk, deleteMsg);
            yield break;
        }

        bool createAlreadyConfirmed = op.Type == OpType.Create &&
                                      !string.IsNullOrWhiteSpace(op.ConfirmedRemoteId);
        bool hasLocalFeature = byId.TryGetValue(op.Id, out Feature feature) && feature != null;

        if (!hasLocalFeature && !createAlreadyConfirmed)
        {
            callback?.Invoke(op.Type == OpType.Update, op.Type == OpType.Update ? "Feature removed locally" : "Feature missing locally");
            yield break;
        }

        if (!createAlreadyConfirmed && !IsFeatureValidForSync(feature))
        {
            callback?.Invoke(false, "Invalid coordinates");
            yield break;
        }

        if (op.Type == OpType.Create)
        {
            if (!createAlreadyConfirmed)
            {
                bool done = false;
                bool ok = false;
                string remoteId = null;

                yield return ApiCreate(feature, (success, returnedId, _raw) =>
                {
                    done = true;
                    ok = success;
                    remoteId = returnedId;
                });

                if (!done || !ok)
                {
                    callback?.Invoke(false, "Create API failed");
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(remoteId))
                {
                    callback?.Invoke(false, "Create API did not return feature.id");
                    yield break;
                }

                op.ConfirmedRemoteId = remoteId.Trim();
            }

            string resolved = op.ConfirmedRemoteId;

            if (!string.Equals(op.Id, resolved, StringComparison.OrdinalIgnoreCase))
            {
                bool rewriteDone = false;
                bool rewriteOk = false;
                string rewriteMessage = string.Empty;

                yield return RewriteLocalFeatureId(op.Id, resolved, (success, message) =>
                {
                    rewriteDone = true;
                    rewriteOk = success;
                    rewriteMessage = message;
                });

                if (!rewriteDone || !rewriteOk)
                {
                    callback?.Invoke(false, rewriteDone ? rewriteMessage : "Local ID rewrite did not complete");
                    yield break;
                }
            }
            else
            {
                MapId(op.Id, resolved);
            }

            yield return NotifyStatusEndpointForLocalChange();
            callback?.Invoke(true, "OK");
            yield break;
        }

        if (op.Type == OpType.Update)
        {
            string canonicalId = feature.id;
            if (string.IsNullOrWhiteSpace(canonicalId))
            {
                callback?.Invoke(false, "Feature missing top-level id for PATCH");
                yield break;
            }

            string remoteId = ResolveRemoteId(canonicalId);
            if (string.IsNullOrWhiteSpace(remoteId))
                remoteId = canonicalId;

            bool updateOk = false;
            string updateMsg = string.Empty;
            yield return ApiUpdate(remoteId, feature, (ok, msg) =>
            {
                updateOk = ok;
                updateMsg = msg;
            });

            if (updateOk)
                yield return NotifyStatusEndpointForLocalChange();

            callback?.Invoke(updateOk, updateMsg);
            yield break;
        }

        callback?.Invoke(false, "Unknown operation");
    }

    private IEnumerator RemotePollingLoop()
    {
        while (enableRemotePolling)
        {
            yield return new WaitForSeconds(Mathf.Max(2f, remotePollingIntervalSeconds));

            if (fetchOnlyWhenExternalUpdates)
            {
                bool statusDone = false;
                bool hasExternalUpdates = false;
                string statusMessage = string.Empty;

                yield return CheckExternalUpdates((done, hasUpdates, message) =>
                {
                    statusDone = done;
                    hasExternalUpdates = hasUpdates;
                    statusMessage = message;
                });

                if (!statusDone)
                {
                    if (!fallbackToFetchWhenStatusUnavailable)
                    {
                        if (verboseLogs)
                            Debug.LogWarning("[GeoJsonSyncCoordinator] Skipped polling refresh: status endpoint unavailable.");
                        continue;
                    }

                    if (verboseLogs)
                        Debug.LogWarning($"[GeoJsonSyncCoordinator] Status check unavailable; proceeding with fetch. Reason: {statusMessage}");
                }
                else if (!hasExternalUpdates)
                {
                    if (verboseLogs)
                        Debug.Log("[GeoJsonSyncCoordinator] Skipped polling refresh: no FE/mobile updates.");
                    continue;
                }
            }

            yield return RefreshRemote(forceApply: false);
        }
    }

    private IEnumerator LatestMqttPollingLoop()
    {
        while (enableLatestMqttPolling)
        {
            yield return new WaitForSeconds(Mathf.Max(1f, latestMqttPollingIntervalSeconds));
            yield return RefreshLatestMqttIcon();
        }
    }

    private IEnumerator RefreshRemote(bool forceApply)
    {
        if (apiManager == null || jsonSpawner == null)
            yield break;

        if (remoteRefreshInProgress)
        {
            QueueDeferredRefresh(forceApply, "another remote refresh is already in progress");
            yield break;
        }

        if (IsRefreshSafetyBlocked(forceApply, out string initialBlockReason))
        {
            QueueDeferredRefresh(forceApply, initialBlockReason);
            yield break;
        }

        remoteRefreshInProgress = true;
        try
        {
            int expectedLocalVersion = jsonSpawner.LocalPersistenceVersion;

            bool done = false;
            bool ok = false;
            string msg = string.Empty;
            Root remoteRoot = null;

            yield return ApiFetch((success, message, root) =>
            {
                done = true;
                ok = success;
                msg = message;
                remoteRoot = root;
            });

            if (!done || !ok || remoteRoot == null)
            {
                if (forceApply)
                    NotifyStatus("Synchronization failed");
                if (verboseLogs)
                    Debug.LogWarning($"[GeoJsonSyncCoordinator] Refresh failed: {msg}");
                yield break;
            }

            CanonicalizeRoot(remoteRoot);
            if (deduplicateFetchedFeatures)
            {
                int removed = DeduplicateFeatures(remoteRoot);
                if (removed > 0 && verboseLogs)
                    Debug.LogWarning($"[GeoJsonSyncCoordinator] Removed {removed} duplicate feature(s) from remote payload before apply.");
            }

            string remoteJson = JsonConvert.SerializeObject(remoteRoot, Formatting.Indented);
            string remoteHash = Hash128.Compute(remoteJson).ToString();

            if (!forceApply && string.Equals(remoteHash, lastRemoteJsonHash, StringComparison.Ordinal))
                yield break;

            if (IsRefreshSafetyBlocked(forceApply, out string commitBlockReason))
            {
                QueueDeferredRefresh(forceApply, commitBlockReason);
                yield break;
            }

            suppressLocalWatcher = true;
            try
            {
                LocalWriteResult writeResult = WriteLocalJson(remoteJson, expectedLocalVersion);
                if (writeResult == LocalWriteResult.LocalStateChanged)
                {
                    QueueDeferredRefresh(forceApply, "local persistence changed while the remote snapshot was being fetched");
                    yield break;
                }

                if (writeResult == LocalWriteResult.Failed)
                {
                    NotifyStatus("Synchronization failed");
                    yield break;
                }

                jsonSpawner.SpawnFromJson();
            }
            finally
            {
                suppressLocalWatcher = false;
            }

            if (TryLoadLocalSnapshot(out Root newLocal, out string newHash))
            {
                lastLocalRoot = newLocal;
                lastLocalJsonHash = newHash;
            }

            RebuildIdMapFromRoot(remoteRoot);
            lastRemoteJsonHash = remoteHash;

            if (resetArUpdatedAfterRemoteRefresh)
                yield return NotifyStatusEndpointForRemoteRefresh();

            NotifyStatus("Synchronization completed");
        }
        finally
        {
            remoteRefreshInProgress = false;

            if (queuedRefresh && deferredRefreshRoutine == null && isActiveAndEnabled)
                deferredRefreshRoutine = StartCoroutine(DeferredRefreshLoop());
        }
    }

    private void QueueDeferredRefresh(bool forceApply, string reason)
    {
        queuedRefresh = true;
        queuedForceRefresh |= forceApply;

        if (hasUnresolvedLocalSyncFailure)
        {
            Debug.LogWarning("[GeoJsonSyncCoordinator] Remote refresh is blocked because a local REST sync operation exhausted its retries. Local data remains unchanged.");
        }
        else if (verboseLogs)
        {
            Debug.LogWarning($"[GeoJsonSyncCoordinator] Remote refresh deferred: {reason}.");
        }

        if (deferredRefreshRoutine == null && isActiveAndEnabled)
            deferredRefreshRoutine = StartCoroutine(DeferredRefreshLoop());
    }

    private IEnumerator DeferredRefreshLoop()
    {
        while (queuedRefresh)
        {
            yield return new WaitForSeconds(Mathf.Max(0.05f, localScanIntervalSeconds));

            if (remoteRefreshInProgress)
                continue;

            bool forceApply = queuedForceRefresh;
            if (IsRefreshSafetyBlocked(forceApply, out _))
                continue;

            queuedRefresh = false;
            queuedForceRefresh = false;
            deferredRefreshRoutine = null;
            StartCoroutine(RefreshRemote(forceApply));
            yield break;
        }

        deferredRefreshRoutine = null;
    }

    private bool IsRefreshSafetyBlocked(bool forceApply, out string reason)
    {
        if (hasUnresolvedLocalSyncFailure)
        {
            reason = "a local REST sync operation exhausted its retries";
            return true;
        }

        if (skipRefreshWhileManipulating && HasActiveManipulation())
        {
            reason = "marker manipulation is active";
            return true;
        }

        if (HasPendingLocalChanges())
        {
            reason = "local persistence or REST synchronization is pending";
            return true;
        }

        if (!forceApply && IsWithinLocalChangeGracePeriod(out double sinceLocalChange))
        {
            reason = $"the local-change grace window is active ({sinceLocalChange:F2}s)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool IsWithinLocalChangeGracePeriod(out double sinceLocalChange)
    {
        sinceLocalChange = (DateTime.UtcNow - lastLocalChangeUtc).TotalSeconds;
        return localChangeGraceSeconds > 0f &&
               sinceLocalChange >= 0d &&
               sinceLocalChange < localChangeGraceSeconds;
    }

    private IEnumerator RefreshLatestMqttIcon()
    {
        if (apiManager == null || jsonSpawner == null)
            yield break;

        bool done = false;
        bool ok = false;
        string msg = string.Empty;
        MqttLatestResponse latest = null;

        yield return ApiFetchLatestMqtt((success, message, response) =>
        {
            done = true;
            ok = success;
            msg = message;
            latest = response;
        });

        if (!done || !ok || latest == null || latest.message == null)
        {
            if (verboseLogs)
                Debug.LogWarning($"[GeoJsonSyncCoordinator] Latest MQTT refresh failed: {msg}");
            yield break;
        }

        bool spawned = jsonSpawner.SpawnLatestMqttIcon(latest);
        if (verboseLogs)
        {
            if (spawned)
                Debug.Log("[GeoJsonSyncCoordinator] Latest MQTT icon refreshed.");
            else
                Debug.LogWarning("[GeoJsonSyncCoordinator] Latest MQTT icon spawn returned false.");
        }
    }

    private bool HasPendingLocalChanges()
    {
        return (jsonSpawner != null && jsonSpawner.HasPendingLocalPersistence) ||
               queue.Count > 0 ||
               queueRunning ||
               updateDebounceTokens.Count > 0 ||
               hasUnresolvedLocalSyncFailure;
    }

    private void RebuildIdMapFromRoot(Root root)
    {
        idMapLocalToRemote.Clear();

        if (root?.features == null)
            return;

        foreach (Feature feature in root.features)
        {
            string id = GetFeatureId(feature);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            idMapLocalToRemote[id] = id;
        }
    }

    private string ResolveRemoteId(string localId)
    {
        if (string.IsNullOrWhiteSpace(localId))
            return null;

        return idMapLocalToRemote.TryGetValue(localId, out string remoteId) ? remoteId : null;
    }

    private void MapId(string localId, string remoteId)
    {
        if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(remoteId))
            return;

        idMapLocalToRemote[localId] = remoteId;
    }

    private void UnmapId(string localId)
    {
        if (string.IsNullOrWhiteSpace(localId))
            return;

        idMapLocalToRemote.Remove(localId);
    }

    private IEnumerator RewriteLocalFeatureId(string oldId, string newId, Action<bool, string> callback)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId))
        {
            callback?.Invoke(false, "Local ID rewrite requires non-empty old and new IDs");
            yield break;
        }

        while (true)
        {
            while (jsonSpawner != null &&
                   (jsonSpawner.HasPendingLocalPersistence ||
                    (skipRefreshWhileManipulating && HasActiveManipulation())))
            {
                yield return null;
            }

            if (jsonSpawner == null)
            {
                callback?.Invoke(false, "JsonSpawner became unavailable during local ID rewrite");
                yield break;
            }

            int expectedLocalVersion = jsonSpawner.LocalPersistenceVersion;

            if (!TryLoadLocalSnapshot(out Root root, out string loadedHash))
            {
                callback?.Invoke(false, "Cannot read the latest local GeoJSON for ID rewrite");
                yield break;
            }

            Dictionary<string, Feature> byId = BuildFeatureMap(root);
            if (!byId.TryGetValue(oldId, out Feature feature) || feature == null)
            {
                if (byId.TryGetValue(newId, out Feature committedFeature) && committedFeature != null)
                {
                    lastLocalRoot = root;
                    lastLocalJsonHash = loadedHash;
                    MapId(newId, newId);
                    UnmapId(oldId);
                    jsonSpawner.SpawnFromJson();
                    callback?.Invoke(true, "Local ID rewrite was already committed");
                    yield break;
                }

                callback?.Invoke(false, $"Local feature '{oldId}' was not found for ID rewrite");
                yield break;
            }

            feature.id = newId;
            if (feature.properties == null)
                feature.properties = new Properties();
            feature.properties.id = newId;

            string json;
            try
            {
                json = JsonConvert.SerializeObject(root, Formatting.Indented);
            }
            catch (Exception ex)
            {
                callback?.Invoke(false, $"Failed to serialize local ID rewrite: {ex.Message}");
                yield break;
            }

            LocalWriteResult writeResult;
            suppressLocalWatcher = true;
            try
            {
                writeResult = WriteLocalJson(json, expectedLocalVersion);
                if (writeResult == LocalWriteResult.Success)
                    jsonSpawner.SpawnFromJson();
            }
            finally
            {
                suppressLocalWatcher = false;
            }

            if (writeResult == LocalWriteResult.LocalStateChanged)
            {
                yield return null;
                continue;
            }

            if (writeResult == LocalWriteResult.Failed)
            {
                callback?.Invoke(false, "Atomic local ID rewrite failed");
                yield break;
            }

            if (!TryLoadLocalSnapshot(out Root refreshedRoot, out string refreshedHash))
            {
                callback?.Invoke(false, "Local ID rewrite committed but could not be reloaded for confirmation");
                yield break;
            }

            lastLocalRoot = refreshedRoot;
            lastLocalJsonHash = refreshedHash;
            MapId(newId, newId);
            UnmapId(oldId);
            callback?.Invoke(true, "OK");
            yield break;
        }
    }

    private static bool AreFeaturesEquivalent(Feature a, Feature b)
    {
        if (a == null || b == null)
            return false;

        string aj = JsonConvert.SerializeObject(a, Formatting.None);
        string bj = JsonConvert.SerializeObject(b, Formatting.None);
        return string.Equals(aj, bj, StringComparison.Ordinal);
    }

    private static Dictionary<string, Feature> BuildFeatureMap(Root root)
    {
        Dictionary<string, Feature> map = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);

        if (root?.features == null)
            return map;

        foreach (Feature feature in root.features)
        {
            string id = GetFeatureId(feature);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            map[id] = feature;
        }

        return map;
    }

    private static string GetFeatureId(Feature feature)
    {
        if (feature == null)
            return string.Empty;

        return feature.id ?? string.Empty;
    }

    private bool TryLoadLocalSnapshot(out Root root, out string jsonHash)
    {
        root = null;
        jsonHash = string.Empty;

        if (!TryReadLocalJson(out string json, out string currentHash))
            return false;

        if (!TryDeserializeLocalSnapshot(json, out root))
            return false;

        jsonHash = currentHash;
        return true;
    }

    private bool TryReadLocalJson(out string json, out string jsonHash)
    {
        json = string.Empty;
        jsonHash = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(saveFileAbsolutePath) || !File.Exists(saveFileAbsolutePath))
                return false;

            json = File.ReadAllText(saveFileAbsolutePath);
            jsonHash = Hash128.Compute(json).ToString();
            return true;
        }
        catch
        {
            json = string.Empty;
            jsonHash = string.Empty;
            return false;
        }
    }

    private static bool TryDeserializeLocalSnapshot(string json, out Root root)
    {
        root = null;

        try
        {
            root = JsonConvert.DeserializeObject<Root>(json);
            if (root == null)
                return false;

            if (root.features == null)
                root.features = new List<Feature>();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private LocalWriteResult WriteLocalJson(string json, int expectedLocalVersion)
    {
        try
        {
            if (jsonSpawner == null)
                jsonSpawner = GetComponent<JsonSpawner>();

            if (jsonSpawner == null)
                return LocalWriteResult.Failed;

            return jsonSpawner.TryWriteExternalJsonSnapshotAtomically(json, expectedLocalVersion)
                ? LocalWriteResult.Success
                : LocalWriteResult.LocalStateChanged;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GeoJsonSyncCoordinator] WriteLocalJson failed: {ex.Message}");
            return LocalWriteResult.Failed;
        }
    }

    private IEnumerator ApiFetch(Action<bool, string, Root> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, "ApiManager missing", null);
            yield break;
        }

        bool completed = false;
        bool ok = false;
        string msg = "Timeout waiting FetchAllFeatures callback";
        Root rootResult = null;

        apiManager.FetchAllFeatures((success, message, root) =>
        {
            completed = true;
            ok = success;
            msg = message;
            rootResult = root;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!completed)
            callback?.Invoke(false, msg, null);
        else
            callback?.Invoke(ok, msg, rootResult);

        yield break;
    }

    private IEnumerator ApiFetchLatestMqtt(Action<bool, string, MqttLatestResponse> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, "ApiManager missing", null);
            yield break;
        }

        bool completed = false;
        bool ok = false;
        string msg = "Timeout waiting latest MQTT GET callback";
        MqttLatestResponse response = null;

        apiManager.FetchLatestMqttStatus((success, message, latest) =>
        {
            completed = true;
            ok = success;
            msg = message;
            response = latest;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!completed)
            callback?.Invoke(false, msg, null);
        else
            callback?.Invoke(ok, msg, response);

        yield break;
    }

    private IEnumerator ApiCreate(Feature feature, Action<bool, string, JObject> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, "", null);
            yield break;
        }

        bool completed = false;
        bool ok = false;
        string returnedId = null;
        JObject rawResult = null;

        apiManager.CreateFeature(feature, (success, id, raw) =>
        {
            completed = true;
            ok = success;
            returnedId = id;
            rawResult = raw;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!completed)
            callback?.Invoke(false, string.Empty, null);
        else
            callback?.Invoke(ok, returnedId, rawResult);

        yield break;
    }

    private IEnumerator ApiUpdate(string featureId, Feature feature, Action<bool, string> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, "ApiManager missing");
            yield break;
        }

        bool completed = false;
        bool ok = false;
        string msg = "Timeout waiting UpdateFeature callback";

        apiManager.UpdateFeature(featureId, feature, (success, message) =>
        {
            completed = true;
            ok = success;
            msg = message;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        callback?.Invoke(completed && ok, completed ? msg : "Timeout waiting UpdateFeature callback");

        yield break;
    }

    private IEnumerator ApiDelete(string featureId, Action<bool, string> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, "ApiManager missing");
            yield break;
        }

        bool completed = false;
        bool ok = false;
        string msg = "Timeout waiting DeleteFeature callback";

        apiManager.DeleteFeature(featureId, (success, message) =>
        {
            completed = true;
            ok = success;
            msg = message;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        callback?.Invoke(completed && ok, completed ? msg : "Timeout waiting DeleteFeature callback");

        yield break;
    }

    private static bool IsFeatureValidForSync(Feature feature)
    {
        if (feature?.geometry?.coordinates == null)
            return false;

        if (!TryExtractFirstCoordinateTriplet(feature.geometry.coordinates, out double lon, out double lat, out double alt))
            return false;

        return IsValidCoordinateSet(lat, lon, alt);
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
            if (arr.Count >= 2 && TryGetDouble(arr[0], out lon) && TryGetDouble(arr[1], out lat))
            {
                if (arr.Count >= 3 && TryGetDouble(arr[2], out double parsedAlt))
                    alt = parsedAlt;

                return true;
            }

            foreach (JToken child in arr)
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

    private void NotifyStatus(string status)
    {
        OnSyncStatus?.Invoke(status);
    }

    private void MarkLocalChangeObserved()
    {
        lastLocalChangeUtc = DateTime.UtcNow;
    }

    private IEnumerator CheckExternalUpdates(Action<bool, bool, string> callback)
    {
        if (apiManager == null)
        {
            callback?.Invoke(false, false, "ApiManager missing");
            yield break;
        }

        bool done = false;
        bool ok = false;
        bool hasExternal = false;
        string msg = "Timeout waiting status GET";

        apiManager.GetCurrentStatus((success, message, status) =>
        {
            done = true;
            ok = success;
            msg = message;
            hasExternal = status != null && (status.fe_updated == 1 || status.mobile_updated == 1);
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!done && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!done)
        {
            callback?.Invoke(false, false, msg);
            yield break;
        }

        callback?.Invoke(ok, hasExternal, msg);
    }

    private static void CanonicalizeRoot(Root root)
    {
        if (root == null)
            return;

        if (root.features == null)
            root.features = new List<Feature>();

        foreach (Feature feature in root.features)
        {
            if (feature == null)
                continue;

            if (feature.properties == null)
                feature.properties = new Properties();

            if (!string.IsNullOrWhiteSpace(feature.id) && string.IsNullOrWhiteSpace(feature.properties.id))
                feature.properties.id = feature.id;
        }

        root.features = root.features
            .Where(f => f != null)
            .OrderBy(f => GetFeatureId(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.geometry?.type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.properties?.className ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int DeduplicateFeatures(Root root)
    {
        if (root?.features == null || root.features.Count <= 1)
            return 0;

        List<Feature> unique = new List<Feature>(root.features.Count);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Feature feature in root.features)
        {
            if (feature == null)
                continue;

            string key = BuildFeatureDedupKey(feature);
            if (string.IsNullOrWhiteSpace(key))
            {
                unique.Add(feature);
                continue;
            }

            if (seen.Add(key))
                unique.Add(feature);
        }

        int removed = root.features.Count - unique.Count;
        root.features = unique;
        return removed;
    }

    private static string BuildFeatureDedupKey(Feature feature)
    {
        if (feature == null)
            return string.Empty;

        string id = GetFeatureId(feature);
        if (!string.IsNullOrWhiteSpace(id))
            return "id:" + id.Trim();

        string geometry = feature.geometry != null
            ? JsonConvert.SerializeObject(feature.geometry, Formatting.None)
            : string.Empty;

        string cls = feature.properties?.className ?? string.Empty;
        return $"nogid:{feature.type}|{cls}|{geometry}";
    }

    private bool HasActiveManipulation()
    {
        FloatingIcon[] icons = FindObjectsByType<FloatingIcon>(FindObjectsSortMode.None);
        if (icons == null || icons.Length == 0)
            return false;

        foreach (FloatingIcon icon in icons)
        {
            if (icon == null || !icon.isActiveAndEnabled)
                continue;

            if (icon.IsBeingManipulated)
                return true;
        }

        return false;
    }

    private IEnumerator NotifyStatusEndpointForLocalChange()
    {
        if (apiManager == null)
            yield break;

        bool done = false;
        bool ok = false;
        string msg = string.Empty;

        apiManager.PatchCurrentStatusFlags(0, 0, 1, (success, message, _status) =>
        {
            done = true;
            ok = success;
            msg = message;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!done && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (verboseLogs)
        {
            if (!done)
                Debug.LogWarning("[GeoJsonSyncCoordinator] Status PATCH timed out.");
            else if (!ok)
                Debug.LogWarning($"[GeoJsonSyncCoordinator] Status PATCH failed: {msg}");
            else
                Debug.Log("[GeoJsonSyncCoordinator] Status PATCH success: fe_updated=0, mobile_updated=0, ar_updated=1.");
        }
    }

    private IEnumerator NotifyStatusEndpointForRemoteRefresh()
    {
        if (apiManager == null)
            yield break;

        if (string.IsNullOrWhiteSpace(remoteRefreshStatusField))
            yield break;

        bool done = false;
        bool ok = false;
        string msg = string.Empty;

        apiManager.PatchCurrentStatusField(remoteRefreshStatusField, remoteRefreshStatusValue, (success, message, _status) =>
        {
            done = true;
            ok = success;
            msg = message;
        });

        float timeout = Mathf.Max(0.1f, apiCallbackTimeoutSeconds);
        float elapsed = 0f;
        while (!done && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (verboseLogs)
        {
            if (!done)
                Debug.LogWarning("[GeoJsonSyncCoordinator] Remote-refresh status PATCH timed out.");
            else if (!ok)
                Debug.LogWarning($"[GeoJsonSyncCoordinator] Remote-refresh status PATCH failed: {msg}");
            else
                Debug.Log($"[GeoJsonSyncCoordinator] Remote-refresh status PATCH success: {remoteRefreshStatusField}={remoteRefreshStatusValue}.");
        }
    }
}
