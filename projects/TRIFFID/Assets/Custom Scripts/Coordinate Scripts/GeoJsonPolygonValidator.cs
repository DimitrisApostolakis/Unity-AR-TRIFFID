using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Site-local ring validation. No Unity dependency, so exactly this code is also
// exercised by the headless regression runner. Hole containment is not inferred.
public static class GeoJsonPolygonValidator
{
    private const double DegreesToRadians = Math.PI / 180d;
    private const double EarthRadius = 6378137d;

    public static bool TryNormalizeFeature(Feature feature, out string error)
    {
        error = string.Empty;
        if (feature == null || feature.geometry == null) return true;
        string type = feature.geometry.type;
        if (type == "Polygon")
            return NormalizePolygon(feature.geometry.coordinates as JArray,
                feature.properties == null ? null : feature.properties.heights_above_surface_m as JArray,
                feature.properties == null ? null : feature.properties.heightSources as JArray,
                feature.properties == null ? null : feature.properties.heightStatuses as JArray, out error);
        if (type == "MultiPolygon")
        {
            JArray polygons = feature.geometry.coordinates as JArray;
            if (polygons == null) { error = "Polygon coordinates are missing."; return false; }
            for (int i = 0; i < polygons.Count; i++)
                if (!NormalizePolygon(polygons[i] as JArray,
                    Child(feature.properties == null ? null : feature.properties.heights_above_surface_m, i),
                    Child(feature.properties == null ? null : feature.properties.heightSources, i),
                    Child(feature.properties == null ? null : feature.properties.heightStatuses, i), out error)) return false;
        }
        return true;
    }

    private static JArray Child(JToken tree, int i)
    {
        JArray array = tree as JArray;
        return array != null && i < array.Count ? array[i] as JArray : null;
    }

    private static bool NormalizePolygon(JArray polygon, JArray heights, JArray sources, JArray statuses, out string error)
    {
        error = string.Empty;
        if (polygon == null || polygon.Count == 0) { error = "Polygon has no exterior ring."; return false; }
        for (int i = 0; i < polygon.Count; i++)
            if (!TryNormalizeRing(polygon[i] as JArray, i == 0, Child(heights, i), Child(sources, i), Child(statuses, i), out error)) return false;
        return true;
    }

    public static bool TryNormalizeRing(JArray ring, bool exterior, out string error)
    {
        return TryNormalizeRing(ring, exterior, null, null, null, out error);
    }

    public static bool TryNormalizeRing(JArray ring, bool exterior, JArray heights, JArray sources, JArray statuses, out string error)
    {
        error = string.Empty;
        if (ring == null || ring.Count < 3) { error = "A polygon needs at least three distinct vertices."; return false; }
        int n = ring.Count;
        if (SameHorizontal(ring[0] as JArray, ring[n - 1] as JArray)) n--;
        if (n < 3) { error = "A polygon needs at least three distinct vertices."; return false; }

        double[] lon = new double[n], lat = new double[n], x = new double[n], y = new double[n];
        double meanLat = 0d, minLon = double.MaxValue, maxLon = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            JArray p = ring[i] as JArray;
            if (p == null || p.Count < 2 || !TryNumber(p[0], out lon[i]) || !TryNumber(p[1], out lat[i]) ||
                lon[i] < -180d || lon[i] > 180d || lat[i] < -90d || lat[i] > 90d)
            { error = "Polygon coordinates must be finite longitude/latitude values."; return false; }
            for (int j = 2; j < p.Count; j++)
            {
                double value;
                if (!TryNumber(p[j], out value)) { error = "Polygon altitude must be finite."; return false; }
            }
            meanLat += lat[i] / n;
            minLon = Math.Min(minLon, lon[i]); maxLon = Math.Max(maxLon, lon[i]);
        }
        double eastScale = EarthRadius * DegreesToRadians * Math.Cos(meanLat * DegreesToRadians);
        if (maxLon - minLon > 180d || Math.Abs(eastScale) < 1e-6)
        { error = "This site-local editor does not support polar or antimeridian-crossing polygons."; return false; }
        double extent = 1d;
        for (int i = 0; i < n; i++)
        {
            x[i] = (lon[i] - lon[0]) * eastScale;
            y[i] = (lat[i] - lat[0]) * EarthRadius * DegreesToRadians;
            extent = Math.Max(extent, Math.Max(Math.Abs(x[i]), Math.Abs(y[i])));
        }
        double lengthEpsilon = Math.Max(1e-6, extent * 1e-12);
        double areaEpsilon = Math.Max(1e-12, extent * extent * 1e-12);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Math.Abs(x[i] - x[j]) <= lengthEpsilon && Math.Abs(y[i] - y[j]) <= lengthEpsilon)
                { error = "Polygon has repeated vertices or a zero-length edge."; return false; }

        double twiceArea = 0d;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n, after = (i + 2) % n;
            twiceArea += x[i] * y[next] - x[next] * y[i];
            double turn = Cross(x[i], y[i], x[next], y[next], x[after], y[after]);
            double dot = (x[next] - x[i]) * (x[after] - x[next]) + (y[next] - y[i]) * (y[after] - y[next]);
            if (Math.Abs(turn) <= areaEpsilon && dot < 0d)
            { error = "Polygon has overlapping adjacent edges."; return false; }
            for (int j = i + 1; j < n; j++)
            {
                int end = (j + 1) % n;
                if (next == j || end == i) continue;
                if (Intersects(x[i], y[i], x[next], y[next], x[j], y[j], x[end], y[end], areaEpsilon, lengthEpsilon))
                { error = "Polygon edges cross or overlap. Move or undo a vertex before saving."; return false; }
            }
        }
        if (Math.Abs(twiceArea) <= areaEpsilon)
        { error = "Polygon vertices do not enclose an area."; return false; }

        bool reverse = exterior ? twiceArea < 0d : twiceArea > 0d;
        List<int> order = new List<int> { 0 };
        for (int i = 1; i < n; i++) order.Add(reverse ? n - i : i);
        Reorder(ring, order, true);
        Reorder(heights, order, false);
        Reorder(sources, order, false);
        Reorder(statuses, order, false);
        return true;
    }

    private static void Reorder(JArray values, List<int> order, bool preserveCoordinateReferences)
    {
        if (values == null) return;
        List<JToken> original = new List<JToken>();
        foreach (JToken value in values) original.Add(value);
        values.Clear();
        foreach (int i in order)
        {
            JToken value = i < original.Count ? original[i] : JValue.CreateNull();
            values.Add(preserveCoordinateReferences ? value : value.DeepClone());
        }
        values.Add(values[0].DeepClone());
    }

    public static bool SameHorizontal(JArray a, JArray b)
    {
        double ax, ay, bx, by;
        return a != null && b != null && a.Count >= 2 && b.Count >= 2 &&
            TryNumber(a[0], out ax) && TryNumber(a[1], out ay) && TryNumber(b[0], out bx) && TryNumber(b[1], out by) &&
            Math.Abs(ax - bx) < 1e-10 && Math.Abs(ay - by) < 1e-10;
    }

    public static bool TryNumber(JToken token, out double value)
    {
        value = 0d;
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
        value = token.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double Cross(double ax, double ay, double bx, double by, double cx, double cy)
    { return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax); }

    private static bool Intersects(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, double areaEpsilon, double lengthEpsilon)
    {
        double a = Cross(ax, ay, bx, by, cx, cy), b = Cross(ax, ay, bx, by, dx, dy);
        double c = Cross(cx, cy, dx, dy, ax, ay), d = Cross(cx, cy, dx, dy, bx, by);
        if (((a > areaEpsilon && b < -areaEpsilon) || (a < -areaEpsilon && b > areaEpsilon)) &&
            ((c > areaEpsilon && d < -areaEpsilon) || (c < -areaEpsilon && d > areaEpsilon))) return true;
        return (Math.Abs(a) <= areaEpsilon && OnSegment(ax, ay, bx, by, cx, cy, lengthEpsilon)) ||
               (Math.Abs(b) <= areaEpsilon && OnSegment(ax, ay, bx, by, dx, dy, lengthEpsilon)) ||
               (Math.Abs(c) <= areaEpsilon && OnSegment(cx, cy, dx, dy, ax, ay, lengthEpsilon)) ||
               (Math.Abs(d) <= areaEpsilon && OnSegment(cx, cy, dx, dy, bx, by, lengthEpsilon));
    }

    private static bool OnSegment(double ax, double ay, double bx, double by, double x, double y, double epsilon)
    { return x >= Math.Min(ax, bx) - epsilon && x <= Math.Max(ax, bx) + epsilon && y >= Math.Min(ay, by) - epsilon && y <= Math.Max(ay, by) + epsilon; }
}
