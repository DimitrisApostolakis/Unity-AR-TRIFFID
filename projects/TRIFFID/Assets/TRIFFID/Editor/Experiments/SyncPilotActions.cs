using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// User-invoked evidence markers. Networking, editing and saving use the normal
// application components; this helper neither acknowledges nor edits the outbox.
public static class SyncPilotActions
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [MenuItem("Tools/Experiments/Synchronization/Record Unchanged Save")]
    private static void SaveUnchanged() { Record(false); }

    [MenuItem("Tools/Experiments/Synchronization/Begin Point Edit")]
    private static void BeginPointEdit() { Record(true); }

    private static void Record(bool edit)
    {
        try
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                throw new InvalidOperationException("Keep Play mode running and UNPAUSED for the network experiment.");
            JsonSpawner source = Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponent<JsonSpawner>();
            if (source == null) throw new InvalidOperationException("Select the JsonSpawner GameObject in the Hierarchy.");
            if ((bool)Get(source, "saveInProgress") || (bool)Get(source, "saveQueued") ||
                (int)Get(source, "dataVersion") != (int)Get(source, "lastSavedVersion") || source.HasActiveAnnotationDraft)
                throw new InvalidOperationException("Finish/cancel drawing, release markers and let local saving settle.");
            Transform map = source.mapTransform != null ? source.mapTransform : source.transform;
            if (Resources.FindObjectsOfTypeAll<FloatingIcon>().Any(x => x.mainMap == map && x.IsBeingManipulated))
                throw new InvalidOperationException("Release markers before recording the action.");
            Root data = (Root)Get(source, "currentRootData");
            if (data == null || data.syncState == null || data.syncState.pending.Count != 0)
                throw new InvalidOperationException("Wait for the pilot fixture to load and its outbox to become empty.");
            string snapshot = Path.GetFullPath(source.saveFilePath);
            string root = Path.GetDirectoryName(snapshot);
            if (Path.GetFileName(snapshot) != "unity_state.json" || !File.Exists(Path.Combine(root, "sync_pilot.json")))
                throw new InvalidOperationException("JsonSpawner Save File Path must be the pilot's unity_state.json, printed by the Python runner.");
            JObject prepared = JObject.Parse(File.ReadAllText(Path.Combine(root, "sync_pilot.json")));
            JObject active = JObject.Parse(File.ReadAllText(Path.Combine(root, "active_run.json")));
            string phase = edit ? "awaiting_edit" : "awaiting_noop_save";
            if (prepared.Value<int>("schema_version") != 1 || active.Value<string>("prepared_id") != prepared.Value<string>("prepared_id") ||
                active.Value<string>("phase") != phase)
                throw new InvalidOperationException("The Python runner is not waiting for this action. Follow its current prompt.");
            string captureName = active.Value<string>("capture");
            if (string.IsNullOrEmpty(captureName) || Path.GetFileName(captureName) != captureName || !captureName.StartsWith("Capture_", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid pilot capture path.");
            string capture = Path.Combine(root, "Captures", captureName);
            if (!Directory.Exists(capture)) throw new DirectoryNotFoundException("The pilot capture directory is missing.");
            GeoJsonSyncCoordinator coordinator = source.GetComponent<GeoJsonSyncCoordinator>();
            if (coordinator == null || !coordinator.isActiveAndEnabled || !ReferenceEquals(Get(coordinator, "jsonSpawner"), source))
                throw new InvalidOperationException("The selected JsonSpawner needs its active, correctly bound GeoJsonSyncCoordinator.");
            GeoJsonApiManager api = (GeoJsonApiManager)Get(coordinator, "apiManager");
            if (api == null || !api.isActiveAndEnabled || !(bool)Get(api, "useLocalhost"))
                throw new InvalidOperationException("Enable Use Localhost on the active GeoJsonApiManager before starting Play mode.");
            int port = prepared.Value<int>("port");
            var endpoints = new JObject();
            foreach (var pair in new[] {
                new[] { "GetFeaturesUrl", "/wp-json/map-manager/v1/features" },
                new[] { "GetStatusUrl", "/wp-json/observer-sync/v1/status" },
                new[] { "GetLatestMqttUrl", "/wp-json/mqtt/v1/latest" } })
            {
                string value = (string)api.GetType().GetMethod(pair[0], Private).Invoke(api, null);
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
                    uri.Port != port || uri.AbsolutePath != pair[1] || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidOperationException("Pilot endpoints must resolve to http://127.0.0.1:" + port + pair[1]);
                endpoints[pair[0]] = value;
            }
            var settings = new JObject();
            foreach (string name in new[] { "loadFromServerOnEnable", "localScanIntervalSeconds", "updateDebounceSeconds",
                "maxRetries", "retryBaseDelaySeconds", "retryMaxDelaySeconds", "enableRemotePolling", "remotePollingIntervalSeconds",
                "enableLatestMqttPolling", "latestMqttPollingIntervalSeconds", "apiCallbackTimeoutSeconds", "localChangeGraceSeconds",
                "fetchOnlyWhenExternalUpdates", "fallbackToFetchWhenStatusUnavailable", "resetArUpdatedAfterRemoteRefresh",
                "remoteRefreshStatusField", "remoteRefreshStatusValue" }) settings[name] = JToken.FromObject(Get(coordinator, name));
            if (!settings.Value<bool>("enableRemotePolling")) throw new InvalidOperationException("Enable remote polling for this pilot.");
            JObject configuration = new JObject { ["schema_version"] = 1, ["run_id"] = active["run_id"],
                ["unity_version"] = Application.unityVersion, ["snapshot"] = snapshot, ["endpoints"] = endpoints,
                ["alignment"] = JToken.FromObject(Get(source, "transformData")),
                ["coordinator"] = settings, ["request_timeout_seconds"] = JToken.FromObject(Get(api, "requestTimeoutSeconds")),
                ["autosave_enabled"] = JToken.FromObject(Get(source, "enableAutoSaveTimer")),
                ["autosave_interval_seconds"] = JToken.FromObject(Get(source, "autoSaveIntervalSeconds")) };
            string configPath = Path.Combine(capture, edit ? "unity_config_edit.json" : "unity_config_noop.json");
            if (File.Exists(configPath)) throw new InvalidOperationException("This action was already recorded. Follow the Python runner.");
            File.WriteAllText(configPath, configuration.ToString(Formatting.Indented), new UTF8Encoding(false));
            JObject action = new JObject { ["run_id"] = active["run_id"], ["kind"] = edit ? "begin_point_edit" : "unchanged_save_requested",
                ["utc"] = DateTime.UtcNow.ToString("O"), ["local_version_before"] = JToken.FromObject(Get(source, "dataVersion")),
                ["features_before"] = JArray.FromObject(data.features) };
            File.AppendAllText(Path.Combine(capture, "unity_actions.jsonl"), action.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
            if (!edit) source.SaveToJson();
            Debug.Log(edit ? "[SyncPilot] Edit start recorded. Move only the target point printed by Python, then release it." :
                "[SyncPilot] Unchanged save requested through JsonSpawner. Keep Play mode running and follow Python.");
        }
        catch (Exception error) { Debug.LogException(error); }
    }

    private static object Get(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, Private);
        if (field == null) throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target);
    }
}
