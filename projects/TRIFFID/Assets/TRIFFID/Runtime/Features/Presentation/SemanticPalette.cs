using UnityEngine;

// One colour vocabulary for the map and the UI. Footprint rings, the info
// panel title and the filter/spawn menu labels all read from here, so a colour
// learned in one place means the same thing everywhere.
public static class SemanticPalette
{
    // Four groups only: few enough to hold in working memory. The values
    // live in TriffidVisualSettings.
    public static Color ActiveHazard => TriffidVisualSettings.Current.activeHazard;
    public static Color Obstacle => TriffidVisualSettings.Current.obstacle;
    public static Color Life => TriffidVisualSettings.Current.life;
    public static Color Asset => TriffidVisualSettings.Current.asset;
    public static Color Neutral => TriffidVisualSettings.Current.neutral;

    // Same thresholds and hues as the percentage text in PointInfoPanel.
    public static readonly Color ConfidenceHigh = new Color(0.29f, 0.87f, 0.50f);
    public static readonly Color ConfidenceMedium = new Color(1.00f, 0.77f, 0.24f);
    public static readonly Color ConfidenceLow = new Color(1.00f, 0.42f, 0.37f);

    public static Color ForClass(string pointClass)
    {
        Color color;
        return TryGetClassColor(pointClass, out color) ? color : Neutral;
    }

    // A class matches the first listed entry whose name it contains, so
    // "fire_small" still reads as fire.
    public static bool TryGetClassColor(string pointClass, out Color color)
    {
        TriffidVisualSettings settings = TriffidVisualSettings.Current;
        color = settings.neutral;
        if (string.IsNullOrWhiteSpace(pointClass) || settings.classes == null)
            return false;

        string token = pointClass.Trim().ToLowerInvariant();
        foreach (ClassStyle style in settings.classes)
        {
            if (style == null || string.IsNullOrWhiteSpace(style.className))
                continue;

            if (!token.Contains(style.className.Trim().ToLowerInvariant()))
                continue;

            color = settings.ColorOf(style.group);
            return true;
        }

        return false;
    }

    // Exact menu captions only, so unrelated text is never recoloured.
    public static bool TryGetMenuLabelColor(string caption, out Color color)
    {
        TriffidVisualSettings settings = TriffidVisualSettings.Current;
        color = settings.neutral;
        if (string.IsNullOrWhiteSpace(caption))
            return false;

        string token = caption.Trim();
        if (string.Equals(token, "safe", System.StringComparison.OrdinalIgnoreCase))
        {
            color = settings.life;
            return true;
        }
        if (string.Equals(token, "unsafe", System.StringComparison.OrdinalIgnoreCase))
        {
            color = settings.activeHazard;
            return true;
        }

        if (settings.classes == null)
            return false;

        foreach (ClassStyle style in settings.classes)
        {
            if (style == null || string.IsNullOrWhiteSpace(style.className) ||
                !string.Equals(token, style.className.Trim(), System.StringComparison.OrdinalIgnoreCase))
                continue;

            color = settings.ColorOf(style.group);
            return true;
        }

        return false;
    }

    public static Color ForConfidence(float confidence01)
    {
        if (confidence01 >= 0.75f) return ConfidenceHigh;
        if (confidence01 >= 0.4f) return ConfidenceMedium;
        return ConfidenceLow;
    }
}
