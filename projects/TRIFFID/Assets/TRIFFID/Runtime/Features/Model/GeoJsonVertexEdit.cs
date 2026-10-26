using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Only a completed user edit may update authoritative geographic vertices.
public static class GeoJsonVertexEdit
{
    public static bool TryApply(Feature feature, IList<JArray> vertices,
        double longitude, double latitude, double altitude, out string error)
    {
        error = null;
        if (feature == null || vertices == null || vertices.Count == 0 ||
            !Finite(longitude) || !Finite(latitude) || !Finite(altitude) ||
            longitude < -180d || longitude > 180d || latitude < -90d || latitude > 90d)
        { error = "Invalid geographic edit."; return false; }
        var saved = new Dictionary<JArray, JArray>();
        foreach (JArray vertex in vertices)
        {
            if (vertex == null || vertex.Count < 2 || !Contains(feature.geometry, vertex))
            { error = "The edited vertex is not part of this feature."; return false; }
            if (!saved.ContainsKey(vertex)) saved.Add(vertex, (JArray)vertex.DeepClone());
        }
        foreach (JArray vertex in saved.Keys)
        {
            vertex[0] = longitude; vertex[1] = latitude;
            if (vertex.Count < 3) vertex.Add(altitude); else vertex[2] = altitude;
        }
        // Validate a copy, retaining winding, tuple references and other vertices.
        bool valid = GeoJsonPolygonValidator.TryNormalizeFeature(GeoJsonOutbox.Clone(feature), out error);
        if (!valid)
            foreach (var pair in saved)
            { pair.Key.Clear(); foreach (JToken value in pair.Value) pair.Key.Add(value.DeepClone()); }
        return valid;
    }

    public static JToken SetVertexValue(JToken tree, JToken coordinates, JArray vertex, JToken value)
    {
        if (ReferenceEquals(coordinates, vertex)) return value.DeepClone();
        JArray array = coordinates as JArray;
        if (array == null || !ContainsToken(array, vertex)) return tree;
        JArray result = tree is JArray ? (JArray)tree.DeepClone() : new JArray();
        while (result.Count < array.Count) result.Add(JValue.CreateNull());
        for (int i = 0; i < array.Count; i++)
            if (ContainsToken(array[i], vertex)) result[i] = SetVertexValue(result[i], array[i], vertex, value);
        return result;
    }

    private static bool Contains(Geometry geometry, JArray vertex)
    {
        if (geometry == null) return false;
        if (ContainsToken(geometry.coordinates, vertex)) return true;
        if (geometry.geometries != null)
            foreach (Geometry child in geometry.geometries) if (Contains(child, vertex)) return true;
        return false;
    }

    private static bool ContainsToken(JToken token, JArray vertex)
    {
        if (ReferenceEquals(token, vertex)) return true;
        JArray array = token as JArray;
        if (array != null) foreach (JToken child in array) if (ContainsToken(child, vertex)) return true;
        return false;
    }

    private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
}
