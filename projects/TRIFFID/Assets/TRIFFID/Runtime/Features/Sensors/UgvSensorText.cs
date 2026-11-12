using System;
using System.Globalization;

// How readings, limits, times and places are written. The menu strip and the
// alert card both use it, so the same value always reads the same.
public static class UgvSensorText
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // "31.5 °C"
    public static string Value(UgvSensorDefinition definition, double apiValue, bool withUnit = true)
    {
        float scale = definition != null ? definition.displayScale : 1f;
        int decimals = definition != null ? Math.Max(0, definition.decimals) : 1;
        string number = (apiValue * scale).ToString("F" + decimals, Culture);

        string unit = definition != null ? definition.unit : null;
        return withUnit && !string.IsNullOrEmpty(unit) ? number + " " + unit : number;
    }

    public static string Name(UgvSensorDefinition definition, string fallbackKey)
    {
        if (definition != null && !string.IsNullOrWhiteSpace(definition.displayName))
            return definition.displayName;
        return fallbackKey;
    }

    // "now", "45 s ago", "12 min ago", "3 h ago", "20 d ago"
    public static string Age(DateTime utc, DateTime utcNow)
    {
        double seconds = (utcNow - utc).TotalSeconds;
        if (seconds < 10.0) return "now";
        if (seconds < 90.0) return ((int)seconds) + " s ago";
        if (seconds < 5400.0) return ((int)(seconds / 60.0)) + " min ago";
        if (seconds < 172800.0) return ((int)(seconds / 3600.0)) + " h ago";
        return ((int)(seconds / 86400.0)) + " d ago";
    }

    // "11:28 UTC" today, "23 Oct 11:28 UTC" on another day.
    public static string Time(DateTime utc, DateTime utcNow)
    {
        return utc.ToString(utc.Date == utcNow.Date ? "HH:mm" : "d MMM HH:mm", Culture) + " UTC";
    }

    // "41.81262, 2.10069", or "no GPS fix" for an alert without a position.
    public static string Location(UgvSensorAlert alert)
    {
        if (alert == null || !alert.hasLocation)
            return "no GPS fix";
        return alert.latitude.ToString("F5", Culture) + ", " + alert.longitude.ToString("F5", Culture);
    }
}
