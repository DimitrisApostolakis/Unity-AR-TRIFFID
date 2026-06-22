using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class GeoJsonApiManager : MonoBehaviour
{
    private const string ApiEndpointsFileName = "api-endpoints.json";

    [Serializable]
    public class ObserverSyncStatus
    {
        public string last_update;
        public int fe_updated;
        public int mobile_updated;
        public int ar_updated;
    }

    [Serializable]
    private class ObserverSyncPatchResponse
    {
        public bool success;
        public ObserverSyncStatus data;
    }

    [Serializable]
    private class ApiEndpointsConfig
    {
        public string productionFeaturesUrl;
        public string productionLatestMqttUrl;
        public string productionStatusUrl;
        public string localhostFeaturesUrl;
        public string localhostLatestMqttUrl;
        public string localhostStatusUrl;
    }

    [Header("API Environment and Requests")]
    [Tooltip("Uses localhost endpoint values from StreamingAssets/api-endpoints.json instead of production values.")]
    [SerializeField] private bool useLocalhost = false;
    [Min(1)]
    [Tooltip("Maximum duration in seconds before an HTTP request times out.")]
    [SerializeField] private int requestTimeoutSeconds = 20;
    [Tooltip("Emits detailed API configuration and request diagnostics.")]
    [SerializeField] private bool verboseLogs = true;

    [Header("Observer Sync Status")]
    [Tooltip("Patches the observer-status endpoint after a successful local feature update.")]
    [SerializeField] private bool enableStatusPatchOnLocalUpdate = true;
    [Tooltip("Observer-status field patched after local GeoJSON changes are synchronized.")]
    [SerializeField] private string statusPatchField = "ar_updated";
    [Tooltip("Value written to the configured observer-status field after a local update.")]
    [SerializeField] private int statusPatchValue = 1;

    private ApiEndpointsConfig apiEndpointsConfig;
    private bool apiConfigLoaded;
    private bool latestMqttEndpointValidationCompleted;
    private bool statusEndpointValidationCompleted;

    private void Awake()
    {
        LoadApiEndpointsConfig();
        ValidateConfiguration();
    }

    public void FetchAllFeatures(Action<bool, string, Root> callback)
    {
        StartCoroutine(FetchAllFeaturesCoroutine(callback));
    }

    public void FetchLatestMqttStatus(Action<bool, string, MqttLatestResponse> callback)
    {
        ValidateSelectedLatestMqttEndpointOnce();
        StartCoroutine(FetchLatestMqttStatusCoroutine(callback));
    }

    public void CreateFeature(Feature feature, Action<bool, string, JObject> callback)
    {
        StartCoroutine(CreateFeatureCoroutine(feature, callback));
    }

    public void UpdateFeature(string featureId, Feature feature, Action<bool, string> callback)
    {
        StartCoroutine(UpdateFeatureCoroutine(featureId, feature, callback));
    }

    public void DeleteFeature(string featureId, Action<bool, string> callback)
    {
        StartCoroutine(DeleteFeatureCoroutine(featureId, callback));
    }

    public void GetCurrentStatus(Action<bool, string, ObserverSyncStatus> callback)
    {
        ValidateSelectedStatusEndpointOnce();
        StartCoroutine(GetCurrentStatusCoroutine(callback));
    }

    public void PatchCurrentStatusFlag(Action<bool, string, ObserverSyncStatus> callback)
    {
        if (!enableStatusPatchOnLocalUpdate)
        {
            callback?.Invoke(true, "Status patch disabled", null);
            return;
        }

        ValidateSelectedStatusEndpointOnce();
        StartCoroutine(PatchCurrentStatusFieldCoroutine(statusPatchField, statusPatchValue, callback));
    }

    public void PatchCurrentStatusField(string fieldName, int value, Action<bool, string, ObserverSyncStatus> callback)
    {
        ValidateSelectedStatusEndpointOnce();
        StartCoroutine(PatchCurrentStatusFieldCoroutine(fieldName, value, callback));
    }

    public void PatchCurrentStatusFlags(int feUpdated, int mobileUpdated, int arUpdated, Action<bool, string, ObserverSyncStatus> callback)
    {
        ValidateSelectedStatusEndpointOnce();
        StartCoroutine(PatchCurrentStatusFlagsCoroutine(feUpdated, mobileUpdated, arUpdated, callback));
    }

    private void LoadApiEndpointsConfig()
    {
        apiEndpointsConfig = new ApiEndpointsConfig();

        string configPath = Path.Combine(Application.streamingAssetsPath, ApiEndpointsFileName);
        string extension = Path.GetExtension(configPath);
        if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            string displayedExtension = string.IsNullOrEmpty(extension) ? "<none>" : extension;
            Debug.LogError($"[GeoJsonApiManager] Invalid API config file type. Expected .json, got {displayedExtension}: {configPath}", this);
            return;
        }

        if (!File.Exists(configPath))
        {
            Debug.LogError($"[GeoJsonApiManager] API config file not found: {configPath}", this);
            return;
        }

        try
        {
            ApiEndpointsConfig loadedConfig = JsonConvert.DeserializeObject<ApiEndpointsConfig>(File.ReadAllText(configPath));
            if (loadedConfig == null)
            {
                Debug.LogError($"[GeoJsonApiManager] API config parsed to null: {configPath}", this);
                return;
            }

            apiEndpointsConfig = loadedConfig;
            apiConfigLoaded = true;

            if (verboseLogs)
                Debug.Log($"[GeoJsonApiManager] Loaded API config from {configPath}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GeoJsonApiManager] Failed to parse API config '{configPath}': {ex.Message}", this);
        }
    }

    private void ValidateConfiguration()
    {
        if (requestTimeoutSeconds <= 0)
            Debug.LogWarning("[GeoJsonApiManager] requestTimeoutSeconds must be positive.", this);

        if (!apiConfigLoaded)
            return;

        ValidateEndpoint(GetSelectedEndpointName("productionFeaturesUrl", "localhostFeaturesUrl"), GetFeaturesUrl());

        if (enableStatusPatchOnLocalUpdate)
            ValidateSelectedStatusEndpointOnce();
    }

    private void ValidateSelectedLatestMqttEndpointOnce()
    {
        if (latestMqttEndpointValidationCompleted)
            return;

        latestMqttEndpointValidationCompleted = true;
        ValidateEndpoint(GetSelectedEndpointName("productionLatestMqttUrl", "localhostLatestMqttUrl"), GetLatestMqttUrl());
    }

    private void ValidateSelectedStatusEndpointOnce()
    {
        if (statusEndpointValidationCompleted)
            return;

        statusEndpointValidationCompleted = true;
        ValidateEndpoint(GetSelectedEndpointName("productionStatusUrl", "localhostStatusUrl"), GetStatusUrl());
    }

    private string GetSelectedEndpointName(string productionName, string localhostName)
    {
        return useLocalhost ? localhostName : productionName;
    }

    private bool ValidateEndpoint(string endpointName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Debug.LogError($"[GeoJsonApiManager] Invalid {endpointName} in {ApiEndpointsFileName}: value is empty.", this);
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Debug.LogError($"[GeoJsonApiManager] Invalid API endpoint URL for {endpointName}: {value}", this);
            return false;
        }

        return true;
    }

    private IEnumerator FetchAllFeaturesCoroutine(Action<bool, string, Root> callback)
    {
        string collectionUrl = GetFeaturesUrl();
        using (UnityWebRequest request = UnityWebRequest.Get(collectionUrl))
        {
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Accept", "application/json");
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("GET", collectionUrl, request), null);
                yield break;
            }

            try
            {
                int droppedMissingClass = 0;
                Root root = ParseRootWithNormalization(request.downloadHandler.text, out droppedMissingClass);
                if (root == null)
                {
                    callback?.Invoke(false, "GET returned empty payload", null);
                    yield break;
                }

                if (root.features == null)
                    root.features = new System.Collections.Generic.List<Feature>();

                if (droppedMissingClass > 0 && verboseLogs)
                    Debug.LogWarning($"[GeoJsonApiManager] Dropped {droppedMissingClass} feature(s) with missing required properties.class.");

                callback?.Invoke(true, "OK", root);
            }
            catch (Exception ex)
            {
                callback?.Invoke(false, $"GET parse error: {ex.Message}", null);
            }
        }
    }

    private IEnumerator FetchLatestMqttStatusCoroutine(Action<bool, string, MqttLatestResponse> callback)
    {
        string resolvedUrl = GetLatestMqttUrl();
        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            callback?.Invoke(false, "Latest MQTT URL is empty", null);
            yield break;
        }

        using (UnityWebRequest request = UnityWebRequest.Get(resolvedUrl))
        {
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Accept", "application/json");
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("GET", resolvedUrl, request), null);
                yield break;
            }

            try
            {
                MqttLatestResponse response = JsonConvert.DeserializeObject<MqttLatestResponse>(request.downloadHandler.text);
                bool valid = response != null && response.message != null && response.message.latitude.HasValue && response.message.longitude.HasValue;
                callback?.Invoke(valid, valid ? "OK" : "Latest MQTT parse returned null", response);
            }
            catch (Exception ex)
            {
                callback?.Invoke(false, $"Latest MQTT GET parse error: {ex.Message}", null);
            }
        }
    }

    private IEnumerator CreateFeatureCoroutine(Feature feature, Action<bool, string, JObject> callback)
    {
        if (feature == null)
        {
            callback?.Invoke(false, string.Empty, null);
            yield break;
        }

        string collectionUrl = GetFeaturesUrl();
        string payload = JsonConvert.SerializeObject(feature, Formatting.None);
        using (UnityWebRequest request = BuildRequest(collectionUrl, "PUT", payload))
        {
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, string.Empty, null);
                if (verboseLogs)
                    Debug.LogWarning(BuildError("PUT", collectionUrl, request));
                yield break;
            }

            JObject body = TryParseJsonObject(request.downloadHandler.text);
            string returnedId = ExtractIdFromCreateResponse(body, feature);
            callback?.Invoke(true, returnedId, body);
        }
    }

    private IEnumerator UpdateFeatureCoroutine(string featureId, Feature feature, Action<bool, string> callback)
    {
        string id = string.IsNullOrWhiteSpace(featureId) ? feature?.id : featureId;
        id = id?.Trim();
        if (string.IsNullOrWhiteSpace(id) || feature == null)
        {
            callback?.Invoke(false, "Invalid update input");
            yield break;
        }

        string url = BuildFeatureItemUrl(GetFeaturesUrl(), id);
        JObject patch = new JObject();

        if (feature.geometry != null)
            patch["geometry"] = JObject.FromObject(feature.geometry);

        if (feature.properties != null)
            patch["properties"] = JObject.FromObject(feature.properties);

        patch["id"] = id;

        string payload = patch.ToString(Formatting.None);
        using (UnityWebRequest request = BuildRequest(url, "PATCH", payload))
        {
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("PATCH", url, request));
                yield break;
            }

            callback?.Invoke(true, "OK");
        }
    }

    private IEnumerator DeleteFeatureCoroutine(string featureId, Action<bool, string> callback)
    {
        string id = featureId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            callback?.Invoke(false, "Invalid delete id");
            yield break;
        }

        string url = BuildFeatureItemUrl(GetFeaturesUrl(), id);
        using (UnityWebRequest request = UnityWebRequest.Delete(url))
        {
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Accept", "application/json");
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("DELETE", url, request));
                yield break;
            }

            callback?.Invoke(true, "OK");
        }
    }

    private IEnumerator GetCurrentStatusCoroutine(Action<bool, string, ObserverSyncStatus> callback)
    {
        string resolvedStatusUrl = GetStatusUrl();
        if (string.IsNullOrWhiteSpace(resolvedStatusUrl))
        {
            callback?.Invoke(false, "Status URL is empty", null);
            yield break;
        }

        using (UnityWebRequest request = UnityWebRequest.Get(resolvedStatusUrl))
        {
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Accept", "application/json");
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("GET", resolvedStatusUrl, request), null);
                yield break;
            }

            try
            {
                ObserverSyncStatus status = JsonConvert.DeserializeObject<ObserverSyncStatus>(request.downloadHandler.text);
                callback?.Invoke(status != null, status != null ? "OK" : "Status parse returned null", status);
            }
            catch (Exception ex)
            {
                callback?.Invoke(false, $"Status GET parse error: {ex.Message}", null);
            }
        }
    }

    private IEnumerator PatchCurrentStatusFieldCoroutine(string fieldName, int value, Action<bool, string, ObserverSyncStatus> callback)
    {
        string resolvedStatusUrl = GetStatusUrl();
        if (string.IsNullOrWhiteSpace(resolvedStatusUrl))
        {
            callback?.Invoke(false, "Status URL is empty", null);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            callback?.Invoke(false, "Status field name is empty", null);
            yield break;
        }

        JObject payloadObj = new JObject
        {
            [fieldName] = value
        };

        using (UnityWebRequest request = BuildRequest(resolvedStatusUrl, "PATCH", payloadObj.ToString(Formatting.None)))
        {
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("PATCH", resolvedStatusUrl, request), null);
                yield break;
            }

            ObserverSyncStatus status = null;
            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            try
            {
                ObserverSyncPatchResponse wrapped = JsonConvert.DeserializeObject<ObserverSyncPatchResponse>(body);
                if (wrapped != null)
                    status = wrapped.data;

                if (status == null)
                    status = JsonConvert.DeserializeObject<ObserverSyncStatus>(body);
            }
            catch
            {
                status = null;
            }

            callback?.Invoke(true, "OK", status);
        }
    }

    private IEnumerator PatchCurrentStatusFlagsCoroutine(int feUpdated, int mobileUpdated, int arUpdated, Action<bool, string, ObserverSyncStatus> callback)
    {
        string resolvedStatusUrl = GetStatusUrl();
        if (string.IsNullOrWhiteSpace(resolvedStatusUrl))
        {
            callback?.Invoke(false, "Status URL is empty", null);
            yield break;
        }

        JObject payloadObj = new JObject
        {
            ["fe_updated"] = feUpdated,
            ["mobile_updated"] = mobileUpdated,
            ["ar_updated"] = arUpdated
        };

        using (UnityWebRequest request = BuildRequest(resolvedStatusUrl, "PATCH", payloadObj.ToString(Formatting.None)))
        {
            yield return request.SendWebRequest();

            if (!IsSuccess(request))
            {
                callback?.Invoke(false, BuildError("PATCH", resolvedStatusUrl, request), null);
                yield break;
            }

            ObserverSyncStatus status = null;
            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            try
            {
                ObserverSyncPatchResponse wrapped = JsonConvert.DeserializeObject<ObserverSyncPatchResponse>(body);
                if (wrapped != null)
                    status = wrapped.data;

                if (status == null)
                    status = JsonConvert.DeserializeObject<ObserverSyncStatus>(body);
            }
            catch
            {
                status = null;
            }

            callback?.Invoke(true, "OK", status);
        }
    }

    private UnityWebRequest BuildRequest(string url, string method, string payload)
    {
        UnityWebRequest request = new UnityWebRequest(url, method);
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payload ?? "{}");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.timeout = Mathf.Max(1, requestTimeoutSeconds);
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "application/json");
        return request;
    }

    private string GetFeaturesUrl()
    {
        string resolvedUrl = useLocalhost
            ? apiEndpointsConfig?.localhostFeaturesUrl
            : apiEndpointsConfig?.productionFeaturesUrl;

        return NormalizeUrlBase(resolvedUrl);
    }

    private string GetLatestMqttUrl()
    {
        string resolvedUrl = useLocalhost
            ? apiEndpointsConfig?.localhostLatestMqttUrl
            : apiEndpointsConfig?.productionLatestMqttUrl;

        return NormalizeUrlBase(resolvedUrl);
    }

    private string GetStatusUrl()
    {
        string resolvedUrl = useLocalhost
            ? apiEndpointsConfig?.localhostStatusUrl
            : apiEndpointsConfig?.productionStatusUrl;

        return NormalizeUrlBase(resolvedUrl);
    }

    private static bool IsSuccess(UnityWebRequest request)
    {
        return request.result == UnityWebRequest.Result.Success
               && request.responseCode >= 200
               && request.responseCode < 300;
    }

    private static JObject TryParseJsonObject(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return JObject.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractIdFromCreateResponse(JObject body, Feature fallback)
    {
        string id = body?.Value<string>("id");
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        string nestedId = body?["feature"]?["id"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(nestedId))
            return nestedId;

        if (!string.IsNullOrWhiteSpace(fallback?.id))
            return fallback.id;

        return string.Empty;
    }

    private static string BuildError(string method, string url, UnityWebRequest request)
    {
        string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
        return $"{method} {url} failed ({request.responseCode}): {request.error}. Body: {body}";
    }

    private static string NormalizeUrlBase(string url)
    {
        return (url ?? string.Empty).Trim().TrimEnd('/');
    }

    private static string BuildFeatureItemUrl(string baseUrl, string featureId)
    {
        string normalizedBase = NormalizeUrlBase(baseUrl);
        string normalizedId = (featureId ?? string.Empty).Trim();
        return normalizedBase + "/" + UnityWebRequest.EscapeURL(normalizedId);
    }

    private static Root ParseRootWithNormalization(string json, out int droppedMissingClass)
    {
        droppedMissingClass = 0;

        if (string.IsNullOrWhiteSpace(json))
            return null;

        JToken token = JToken.Parse(json);
        if (!(token is JObject rootObj))
            return null;

        if (!(rootObj["features"] is JArray features))
        {
            features = new JArray();
            rootObj["features"] = features;
        }

        JArray normalizedFeatures = new JArray();
        foreach (JToken featureToken in features)
        {
            if (!(featureToken is JObject featureObj))
                continue;

            JObject propertiesObj = featureObj["properties"] as JObject;
            if (propertiesObj == null)
            {
                propertiesObj = new JObject();
                featureObj["properties"] = propertiesObj;
            }

            string className = propertiesObj["class"]?.Type == JTokenType.Null
                ? string.Empty
                : (propertiesObj["class"]?.ToString() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(className))
            {
                droppedMissingClass++;
                continue;
            }

            propertiesObj["class"] = className;

            NormalizeStringField(propertiesObj, "id");
            NormalizeStringField(propertiesObj, "category");
            NormalizeStringField(propertiesObj, "source");
            NormalizeStringField(propertiesObj, "marker-color");

            NormalizeNumberField(propertiesObj, "confidence", 0d);
            NormalizeNumberField(propertiesObj, "altitude_m", 0d);

            JToken featureId = featureObj["id"];
            if (featureId == null || featureId.Type == JTokenType.Null || string.IsNullOrWhiteSpace(featureId.ToString()))
                continue;

            featureObj["id"] = featureId.ToString().Trim();

            normalizedFeatures.Add(featureObj);
        }

        rootObj["features"] = normalizedFeatures;
        return rootObj.ToObject<Root>();
    }

    private static void NormalizeStringField(JObject obj, string fieldName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        JToken token = obj[fieldName];
        if (token == null || token.Type == JTokenType.Null)
        {
            obj[fieldName] = string.Empty;
            return;
        }

        obj[fieldName] = token.ToString().Trim();
    }

    private static void NormalizeNumberField(JObject obj, string fieldName, double fallback)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        JToken token = obj[fieldName];
        if (token == null || token.Type == JTokenType.Null)
        {
            obj[fieldName] = fallback;
            return;
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            return;

        string raw = token.ToString().Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            obj[fieldName] = parsed;
        else
            obj[fieldName] = fallback;
    }
}
