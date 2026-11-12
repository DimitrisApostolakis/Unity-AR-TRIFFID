using System.Collections.Generic;

// Flat geometry for "is this point inside that outline" questions. Points are
// plain x, y pairs in any one consistent unit; the outline is a closed ring
// given once per corner (a repeated last corner does no harm).
public static class PolygonContainment
{
    public struct Point
    {
        public double x, y;

        public Point(double x, double y)
        {
            this.x = x;
            this.y = y;
        }
    }

    public static bool Contains(IReadOnlyList<Point> ring, Point p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Point a = ring[i], b = ring[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    // p itself when it is inside; otherwise the nearest point of the outline,
    // moved a little inward (inset, as a fraction of the way to the middle of
    // the outline) so that it counts as inside. False for a ring without area.
    public static bool TryClampInside(IReadOnlyList<Point> ring, Point p, double inset, out Point result)
    {
        result = p;
        if (ring == null || ring.Count < 3)
            return false;
        if (Contains(ring, p))
            return true;

        Point nearest = ring[0];
        double best = double.MaxValue;
        double middleX = 0d, middleY = 0d;

        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Point a = ring[j], b = ring[i];
            middleX += b.x;
            middleY += b.y;

            double dx = b.x - a.x, dy = b.y - a.y;
            double length = dx * dx + dy * dy;
            double t = length > 0d ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / length : 0d;
            t = t < 0d ? 0d : (t > 1d ? 1d : t);

            Point candidate = new Point(a.x + dx * t, a.y + dy * t);
            double distance = (candidate.x - p.x) * (candidate.x - p.x) + (candidate.y - p.y) * (candidate.y - p.y);
            if (distance < best)
            {
                best = distance;
                nearest = candidate;
            }
        }

        middleX /= ring.Count;
        middleY /= ring.Count;

        // In a hollow shape the middle can lie outside; then the outline
        // point itself is used.
        Point inward = new Point(nearest.x + (middleX - nearest.x) * inset, nearest.y + (middleY - nearest.y) * inset);
        result = Contains(ring, inward) ? inward : nearest;
        return true;
    }
}
