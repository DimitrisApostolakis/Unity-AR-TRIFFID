// Central console logging for TRIFFID runtime and Editor code.
//
//   TriffidLog.Info("JsonSpawner", "Loaded 42 features.", this);
//   TriffidLog.Warning("MapReset", "Map target is missing; map reset is unavailable.", this);
//   TriffidLog.Verbose("PointInfoPanel", "Registered marker ...");   // hidden unless verbose logging is on
//
// Every message is tagged "[TRIFFID][Source]". In the Editor console the tag is
// bold and coloured per source, so related messages are easy to scan. Player
// builds (e.g. Quest logcat) get the same tag as plain text:
//
//   adb logcat -s Unity | grep "\[TRIFFID\]"
//
// Verbose messages are per-marker / per-state chatter that is useful while
// debugging but noisy otherwise. They are off by default; enable them with
// Tools > TRIFFID > Verbose Logging in the Editor, or by adding the
// TRIFFID_VERBOSE_LOGS scripting define to a player build.
using System;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

public static class TriffidLog
{
    public const string Product = "TRIFFID";

#if TRIFFID_VERBOSE_LOGS
    public static bool VerboseEnabled = true;
#else
    public static bool VerboseEnabled;
#endif

#if UNITY_2022_2_OR_NEWER
    [HideInCallstack]
#endif
    public static void Verbose(string source, string message, Object context = null)
    {
        if (VerboseEnabled) Debug.Log(Format(source, message), context);
    }

#if UNITY_2022_2_OR_NEWER
    [HideInCallstack]
#endif
    public static void Info(string source, string message, Object context = null)
    {
        Debug.Log(Format(source, message), context);
    }

#if UNITY_2022_2_OR_NEWER
    [HideInCallstack]
#endif
    public static void Warning(string source, string message, Object context = null)
    {
        Debug.LogWarning(Format(source, message), context);
    }

#if UNITY_2022_2_OR_NEWER
    [HideInCallstack]
#endif
    public static void Error(string source, string message, Object context = null)
    {
        Debug.LogError(Format(source, message), context);
    }

    /// <summary>
    /// Builds an aligned "label : value" block for multi-line reports, e.g.
    /// <c>TriffidLog.Info(src, "Paths validated." + TriffidLog.Fields(("JSON", path), ("Exists", true)))</c>.
    /// The first line of a message stays the one-line summary shown in the console list.
    /// </summary>
    public static string Fields(params (string label, object value)[] rows)
    {
        if (rows == null || rows.Length == 0) return string.Empty;
        int width = 0;
        foreach (var row in rows) width = Math.Max(width, (row.label ?? string.Empty).Length);

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            string value = row.value == null ? "<none>" : row.value.ToString();
            value = value.Replace("\n", "\n" + new string(' ', width + 7));
            text.Append("\n  • ").Append((row.label ?? string.Empty).PadRight(width)).Append(" : ").Append(value);
        }
        return text.ToString();
    }

    private static string Format(string source, string message)
    {
        if (string.IsNullOrEmpty(source)) source = "General";
#if UNITY_EDITOR
        return "<b><color=" + Colour(source) + ">[" + Product + "][" + source + "]</color></b> " + message;
#else
        return "[" + Product + "][" + source + "] " + message;
#endif
    }

#if UNITY_EDITOR
    // Mid-saturation hues that stay readable on both the light and dark Editor skins.
    private static readonly string[] Palette =
    {
        "#3B9AE1", "#2BAE8E", "#C98A1B", "#9C6ADE", "#D9577A",
        "#4FA33A", "#1FA3B8", "#D4673A", "#7A86E0", "#B0663F",
    };

    private static string Colour(string source)
    {
        // Stable across sessions and platforms (string.GetHashCode is not).
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in source) hash = (hash ^ c) * 16777619;
            return Palette[hash % (uint)Palette.Length];
        }
    }
#endif
}
