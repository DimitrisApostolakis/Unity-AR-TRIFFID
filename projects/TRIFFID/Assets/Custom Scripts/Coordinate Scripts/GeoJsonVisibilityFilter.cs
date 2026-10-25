using System;
using System.Collections.Generic;

// Local view state, independent of saved geographic data and synchronization.
public sealed class GeoJsonVisibilityFilter
{
    private sealed class Rule
    {
        public HashSet<string> tokens;
        public bool visible;
    }
    private readonly List<Rule> rules = new List<Rule>();

    public void SetVisibility(string category, bool visible)
    {
        string token = Normalize(category);
        if (token.Length == 0) return;
        HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { token };
        if (token.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && token.Length > 3)
            tokens.Add(token.Substring(0, token.Length - 3) + "y");
        if (token.EndsWith("y", StringComparison.OrdinalIgnoreCase) && token.Length > 1)
            tokens.Add(token.Substring(0, token.Length - 1) + "ies");
        if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase) && token.Length > 1)
            tokens.Add(token.Substring(0, token.Length - 1));
        else tokens.Add(token + "s");
        rules.RemoveAll(rule => rule.tokens.Overlaps(tokens));
        rules.Add(new Rule { tokens = tokens, visible = visible });
    }

    public bool IsVisible(Feature feature)
    {
        bool visible;
        TryGetVisibility(feature, out visible);
        return visible;
    }

    public bool TryGetVisibility(Feature feature, out bool visible)
    {
        visible = true;
        string type = feature?.geometry?.type;
        if (feature?.properties == null ||
            !(string.Equals(type, "Point", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(type, "MultiPoint", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase))) return false;
        string category = Normalize(feature.properties.category);
        string className = Normalize(feature.properties.className);
        bool matched = false;
        // Preserve the previous control semantics: the latest matching filter
        // action wins when a feature matches more than one category/class rule.
        foreach (Rule rule in rules)
        {
            foreach (string token in rule.tokens)
            {
                if (!category.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
                    !className.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
                visible = rule.visible;
                matched = true;
                break;
            }
        }
        return matched;
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty :
            value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }
}
