using UnityEngine;

// Line colour of the two Mission Planner route kinds, read from the visual
// settings so a drawn route and a loaded one always match.
public static class MissionPathColors
{
    public static Color ColorOf(MissionPathType type)
    {
        TriffidVisualSettings settings = TriffidVisualSettings.Current;
        Color color = type == MissionPathType.Ugv ? settings.ugvPathColor : settings.responderPathColor;
        color.a = 1f;
        return color;
    }

    public static bool TryGetColor(string className, out Color color)
    {
        MissionPathType type;
        if (MissionPaths.TryParse(className, out type))
        {
            color = ColorOf(type);
            return true;
        }

        color = default;
        return false;
    }
}
