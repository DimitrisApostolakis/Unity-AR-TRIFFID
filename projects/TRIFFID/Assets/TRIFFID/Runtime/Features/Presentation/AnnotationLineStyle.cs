using UnityEngine;

// Single entry point for how an annotation line looks. Both places that
// create lines (GeoJSON spawning and controller drawing) call this, so the
// two can never drift apart.
public static class AnnotationLineStyle
{
    public static void Apply(LineRenderer line, bool isPolygon)
    {
        if (line == null)
            return;

        PolygonFill.StyleOutline(line);
        if (isPolygon)
            PolygonFill.Attach(line);
        LineSelectionGlow.Attach(line);
    }
}
