using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// One recorded alert: a reading that reached or passed its limit.
public sealed class UgvSensorAlert
{
    public long id;
    public string sensor;
    public double value;
    public double threshold;
    public double latitude;
    public double longitude;
    // False when the UGV had no GPS signal (the API then reports 0, 0).
    public bool hasLocation;
    public DateTime timestampUtc;
    public bool hasTimestamp;

    // True when this alert was recorded after the other one.
    public bool IsNewerThan(UgvSensorAlert other)
    {
        if (other == null)
            return true;
        if (hasTimestamp && other.hasTimestamp && timestampUtc != other.timestampUtc)
            return timestampUtc > other.timestampUtc;
        return id > other.id;
    }
}

// One answer of the alerts endpoint.
public sealed class UgvSensorAlertPage
{
    public readonly List<UgvSensorAlert> alerts = new List<UgvSensorAlert>();
    // Limits sent along with the alerts; empty when the server sent none.
    public readonly Dictionary<string, double> thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}

// The last known state of one reading.
public sealed class UgvSensorState
{
    public readonly UgvSensorDefinition definition;
    public bool hasThreshold;
    public double threshold;
    // The most recent alert of this reading, or null when none is on record.
    public UgvSensorAlert lastAlert;

    public UgvSensorState(UgvSensorDefinition definition)
    {
        this.definition = definition;
    }

    public UgvSensorLevel LevelAt(DateTime utcNow, UgvSensorSettings settings, bool dataReceived)
    {
        if (!dataReceived)
            return UgvSensorLevel.Unknown;
        if (lastAlert == null)
            return UgvSensorLevel.Normal;
        if (!lastAlert.hasTimestamp)
            return UgvSensorLevel.Earlier;

        double ageSeconds = (utcNow - lastAlert.timestampUtc).TotalSeconds;
        if (ageSeconds <= settings.activeAlertSeconds)
            return UgvSensorLevel.Alert;
        if (settings.forgetAlertAfterHours > 0f && ageSeconds > settings.forgetAlertAfterHours * 3600.0)
            return UgvSensorLevel.Normal;
        return UgvSensorLevel.Earlier;
    }
}

// The alerts one check found to be newer than the last one seen.
public sealed class UgvSensorAlertBatch
{
    // The new alerts, oldest id first. Never empty.
    public readonly List<UgvSensorAlert> alerts = new List<UgvSensorAlert>();
    // Id of the newest alert known before this check, or -1 when there was none.
    public long previousId;

    // The alert with the highest id.
    public UgvSensorAlert Latest { get { return alerts[alerts.Count - 1]; } }
}

// Reads the two API answers. Unknown or malformed entries are skipped, so one
// bad alert never hides the others.
public static class UgvSensorPayloadParser
{
    private static readonly JsonSerializerSettings Raw = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

    // {"co2_eq":600,"temp":29,...}
    public static bool TryParseThresholds(string json, Dictionary<string, double> into)
    {
        JObject root = Parse(json) as JObject;
        if (root == null)
            return false;

        ReadThresholds(root, into);
        return true;
    }

    // {"total":158,"thresholds":{...},"alerts":[{...},...]}
    public static bool TryParseAlerts(string json, out UgvSensorAlertPage page)
    {
        page = null;
        JObject root = Parse(json) as JObject;
        if (root == null)
            return false;

        JArray list = root["alerts"] as JArray;
        if (list == null)
            return false;

        page = new UgvSensorAlertPage();
        ReadThresholds(root["thresholds"] as JObject, page.thresholds);

        foreach (JToken token in list)
        {
            UgvSensorAlert alert = ReadAlert(token as JObject);
            if (alert != null)
                page.alerts.Add(alert);
        }
        return true;
    }

    private static JToken Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try { return JsonConvert.DeserializeObject<JToken>(json, Raw); }
        catch (JsonException) { return null; }
    }

    private static void ReadThresholds(JObject source, Dictionary<string, double> into)
    {
        if (source == null)
            return;

        foreach (JProperty property in source.Properties())
        {
            double limit;
            if (TryNumber(property.Value, out limit))
                into[property.Name] = limit;
        }
    }

    private static UgvSensorAlert ReadAlert(JObject source)
    {
        if (source == null)
            return null;

        double id, value;
        string sensor = (string)source["sensor"];
        if (string.IsNullOrEmpty(sensor) || !TryNumber(source["id"], out id) || !TryNumber(source["value"], out value))
            return null;

        UgvSensorAlert alert = new UgvSensorAlert { id = (long)id, sensor = sensor, value = value };
        TryNumber(source["threshold"], out alert.threshold);
        TryNumber(source["latitude"], out alert.latitude);
        TryNumber(source["longitude"], out alert.longitude);

        JToken gnss = source["gnss_valid"];
        bool gnssValid = gnss == null || gnss.Type != JTokenType.Boolean || (bool)gnss;
        alert.hasLocation = gnssValid && (alert.latitude != 0.0 || alert.longitude != 0.0);

        DateTimeOffset time;
        JToken stamp = source["timestamp"];
        if (stamp != null && stamp.Type == JTokenType.String &&
            DateTimeOffset.TryParse((string)stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time))
        {
            alert.timestampUtc = time.UtcDateTime;
            alert.hasTimestamp = true;
        }
        return alert;
    }

    private static bool TryNumber(JToken token, out double number)
    {
        number = 0.0;
        if (token == null)
            return false;

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            number = (double)token;
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        return token.Type == JTokenType.String &&
            double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
