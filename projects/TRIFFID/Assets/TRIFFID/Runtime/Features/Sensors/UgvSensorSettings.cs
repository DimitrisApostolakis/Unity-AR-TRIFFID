using System;
using System.Collections.Generic;
using UnityEngine;

// How one environmental reading of the UGV is named and written.
[Serializable]
public class UgvSensorDefinition
{
    [Tooltip("Name of the reading in the API (the key in /thresholds and the 'sensor' field of an alert).")]
    public string key;
    [Tooltip("Name shown on the menu and in the alert card.")]
    public string displayName;
    [Tooltip("Unit written after the value. May be empty.")]
    public string unit;
    [Tooltip("The API value is multiplied by this before it is shown, e.g. 0.01 turns Pa into hPa.")]
    public float displayScale = 1f;
    [Range(0, 4)]
    [Tooltip("Decimal places of the shown value.")]
    public int decimals = 1;

    public UgvSensorDefinition() { }

    public UgvSensorDefinition(string key, string displayName, string unit, float displayScale, int decimals)
    {
        this.key = key;
        this.displayName = displayName;
        this.unit = unit;
        this.displayScale = displayScale;
        this.decimals = decimals;
    }
}

// What the colour of a reading says.
public enum UgvSensorLevel
{
    Unknown,    // nothing received from the API yet
    Normal,     // no alert on record: the reading has stayed below its limit
    Earlier,    // the limit was reached earlier; the last alert is shown with its age
    Alert       // the limit was reached within the last few moments
}

// The one place to configure the UGV environmental readings: where they are
// fetched, how they are written, when a reading counts as an alert, and how
// the menu strip and the alert card look.
//
// The asset at Assets/TRIFFID/Resources/UgvSensorSettings.asset is loaded once
// per Play session; without it the built-in defaults below are used.
[CreateAssetMenu(fileName = ResourceName, menuName = "TRIFFID/UGV Sensor Settings")]
public class UgvSensorSettings : ScriptableObject
{
    public const string ResourceName = "UgvSensorSettings";

    [Header("Features")]
    [Tooltip("Master switch. Off: nothing is fetched and nothing is shown.")]
    public bool sensorsEnabled = true;
    [Tooltip("Row of readings at the top of the main menu.")]
    public bool showMenuStrip = true;
    [Tooltip("Card shown when a new alert is recorded.")]
    public bool showAlertNotifications = true;

    [Header("API")]
    [Tooltip("GET: the limit of every reading.")]
    public string thresholdsUrl = "https://crispres.com/wp-json/mqtt/v1/thresholds";
    [Tooltip("GET: recorded alerts, newest first. '?sensor=' and '?limit=' are added by the app.")]
    public string alertsUrl = "https://crispres.com/wp-json/mqtt/v1/alerts";
    [Tooltip("For testing in the Editor: read from the local server (LocalScripts/local_geojson_server.py) instead of the real API. Record a test alert with LocalScripts/local_sensor_alerts.py.")]
    public bool useLocalServer = false;
    public string localThresholdsUrl = "http://127.0.0.1:8080/wp-json/mqtt/v1/thresholds";
    public string localAlertsUrl = "http://127.0.0.1:8080/wp-json/mqtt/v1/alerts";
    [Min(1f)]
    [Tooltip("Seconds between two checks for new alerts.")]
    public float pollSeconds = 5f;
    [Min(5f)]
    [Tooltip("Seconds between two refreshes of the limits.")]
    public float thresholdRefreshSeconds = 60f;
    [Range(1, 200)]
    [Tooltip("Alerts requested per check. Raise it if more alerts than this can arrive between two checks.")]
    public int alertsPerPoll = 50;
    [Min(1)]
    public int requestTimeoutSeconds = 15;

    [Header("Readings")]
    [Tooltip("Readings shown, in menu order. Only alerts of these readings are announced.")]
    public List<UgvSensorDefinition> sensors = new List<UgvSensorDefinition>
    {
        new UgvSensorDefinition("co2_eq", "CO2", "ppm", 1f, 0),
        new UgvSensorDefinition("temp", "Temperature", "°C", 1f, 1),
        new UgvSensorDefinition("humidity", "Humidity", "%", 100f, 2),
        new UgvSensorDefinition("pressure", "Pressure", "hPa", 0.01f, 2),
    };

    [Header("Levels")]
    [Min(1f)]
    [Tooltip("An alert younger than this is shown as happening now (Alert colour).")]
    public float activeAlertSeconds = 120f;
    [Min(0f)]
    [Tooltip("An alert older than this many hours is no longer shown and the reading returns to Normal. Zero keeps the last alert visible for ever.")]
    public float forgetAlertAfterHours = 0f;

    [Header("Level Colours")]
    public Color normalColor = new Color(0.29f, 0.87f, 0.50f);
    public Color earlierColor = new Color(1.00f, 0.77f, 0.24f);
    public Color alertColor = new Color(1.00f, 0.36f, 0.30f);
    public Color unknownColor = new Color(0.62f, 0.67f, 0.70f);

    [Header("Alert Card")]
    [Tooltip("On: the card appears small, low and to the side, without shaking, and never replaces an error card. Off: it appears in the centre like an error.")]
    public bool quietAlertNotifications = true;
    [Tooltip("Colour and mark of the card.")]
    public NotificationKind alertNotificationKind = NotificationKind.Warning;
    [Range(1, 10)]
    [Tooltip("When several alerts arrive together, each gets its own card, one after the other. This is the most cards one check may produce; with more alerts, only the newest are shown and the last card says how many were left out.")]
    public int maxAlertCardsPerCheck = 3;
    [Tooltip("The very first time the app runs there is no previous alert to compare with. On: the latest recorded alert is shown once. Off: it is only remembered.")]
    public bool announceLatestOnFirstRun = true;

    [Header("Menu Strip")]
    [Tooltip("Name of the menu object that receives the strip. It must hold a horizontal or vertical layout group.")]
    public string menuContainerName = "Menu Origin";
    [Min(8f)]
    [Tooltip("Height of the strip in canvas units.")]
    public float stripHeight = 22f;
    [Min(0f)]
    [Tooltip("Space between the strip and the menu buttons below it.")]
    public float stripGap = 3f;
    [Tooltip("Canvas units the strip stands in front of the menu backplate (negative is toward the user). At zero the backplate and the strip are drawn at the same depth and flicker through each other.")]
    public float stripDepthOffset = -2f;
    [Min(0f)]
    [Tooltip("Space between two readings.")]
    public float cellSpacing = 2f;
    [Tooltip("On: the menu grows upward and everything below it stays where it was.")]
    public bool keepMenuBottomEdge = true;
    public Color cellPlateColor = new Color(1f, 1f, 1f, 0.06f);
    [Min(0f)]
    [Tooltip("Width of the level-coloured bar at the left of each reading.")]
    public float cellAccentWidth = 1.2f;
    [Min(1f)]
    public float captionFontSize = 4.2f;
    [Min(1f)]
    public float valueFontSize = 7f;
    [Min(1f)]
    public float detailFontSize = 3.6f;
    [Range(0f, 1f)]
    [Tooltip("Opacity of the caption and of the small line under the value.")]
    public float secondaryTextAlpha = 0.7f;
    [Min(0f)]
    [Tooltip("Seconds a reading glows after a new alert for it arrives. Zero disables the glow.")]
    public float newAlertGlowSeconds = 2.5f;

    // The addresses in use: the local server's when useLocalServer is on.
    public string ThresholdsUrl { get { return useLocalServer ? localThresholdsUrl : thresholdsUrl; } }
    public string AlertsUrl { get { return useLocalServer ? localAlertsUrl : alertsUrl; } }

    public Color ColorOf(UgvSensorLevel level)
    {
        switch (level)
        {
            case UgvSensorLevel.Normal: return normalColor;
            case UgvSensorLevel.Earlier: return earlierColor;
            case UgvSensorLevel.Alert: return alertColor;
            default: return unknownColor;
        }
    }

    public UgvSensorDefinition Find(string key)
    {
        if (string.IsNullOrEmpty(key) || sensors == null)
            return null;

        foreach (UgvSensorDefinition sensor in sensors)
            if (sensor != null && string.Equals(sensor.key, key, StringComparison.OrdinalIgnoreCase))
                return sensor;

        return null;
    }

    private static UgvSensorSettings current;

    public static UgvSensorSettings Current
    {
        get
        {
            if (current == null)
                current = Resources.Load<UgvSensorSettings>(ResourceName);

            // No asset: run on the built-in defaults instead of failing.
            if (current == null)
            {
                current = CreateInstance<UgvSensorSettings>();
                current.hideFlags = HideFlags.HideAndDontSave;
            }

            return current;
        }
    }
}
