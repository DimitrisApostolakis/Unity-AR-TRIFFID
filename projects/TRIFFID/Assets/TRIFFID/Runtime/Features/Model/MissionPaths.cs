using System;

public enum MissionPathType
{
    Responder,
    Ugv
}

// The two planned-route kinds of the Mission Planner. A route is a GeoJSON
// LineString whose "class" is one of the names below; everything that draws,
// saves or labels a route asks here, so the two names live in one place.
// Colours are in MissionPathColors. This file depends on nothing else.
public static class MissionPaths
{
    public const string ResponderClass = "responder_path";
    public const string UgvClass = "ugv_path";

    public static string ClassName(MissionPathType type)
    {
        return type == MissionPathType.Ugv ? UgvClass : ResponderClass;
    }

    public static string DisplayName(MissionPathType type)
    {
        return type == MissionPathType.Ugv ? "UGV Path" : "Responders Path";
    }

    // Accepts the class as written by other producers too: any case, and
    // spaces or hyphens in place of the underscore.
    public static bool TryParse(string className, out MissionPathType type)
    {
        type = MissionPathType.Responder;
        if (string.IsNullOrWhiteSpace(className))
            return false;

        string token = className.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
        if (string.Equals(token, ResponderClass, StringComparison.Ordinal) ||
            string.Equals(token, "responders_path", StringComparison.Ordinal))
        {
            type = MissionPathType.Responder;
            return true;
        }

        if (string.Equals(token, UgvClass, StringComparison.Ordinal))
        {
            type = MissionPathType.Ugv;
            return true;
        }

        return false;
    }

    public static bool IsMissionPath(string className)
    {
        MissionPathType type;
        return TryParse(className, out type);
    }

    public static bool TryGetDisplayName(string className, out string displayName)
    {
        MissionPathType type;
        if (TryParse(className, out type))
        {
            displayName = DisplayName(type);
            return true;
        }

        displayName = null;
        return false;
    }
}
