using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The icon of a polygon (its centre marker) can be dragged, but only within
// the polygon's outer outline.
//
// - While it is held, FloatingIcon calls KeepIconInsidePolygon every frame.
// - When it is released, PlaceIconInsidePolygon anchors it where it was
//   dropped and remembers that place.
// - When a corner of the polygon is moved, SettlePolygonIcon keeps the icon
//   inside the new outline.
// - Markers are rebuilt on every refresh; RestorePolygonIcon puts a rebuilt
//   icon back on its remembered place while that place is still inside.
//
// The place is kept for the session only. GeoJSON has no field for it, so no
// feature data is changed and nothing is sent to the server.
public partial class JsonSpawner
{
    // How far a clamped icon sits inside the outline, as a fraction of the way to the polygon's middle.
    private const double PolygonIconInset = 0.03d;

    private readonly Dictionary<string, Vector3Double> polygonIconPlaces = new Dictionary<string, Vector3Double>();
    private readonly List<PolygonContainment.Point> polygonRingBuffer = new List<PolygonContainment.Point>();

    // Returns true when the node is a polygon icon (whether or not it had to be moved).
    public bool KeepIconInsidePolygon(Transform icon)
    {
        NodeMapping mapping = FindPolygonIconMapping(icon);
        Vector3Double place;
        if (mapping == null || !TryClampToPolygon(mapping, icon.position, out place, out bool moved))
            return false;

        if (moved)
            icon.position = MapTransformOrSelf().TransformPoint(GetColmapPosition(place.lon, place.lat, place.alt));
        return true;
    }

    // Returns true when the node is a polygon icon and has been anchored.
    public bool PlaceIconInsidePolygon(Transform icon)
    {
        NodeMapping mapping = FindPolygonIconMapping(icon);
        Vector3Double place;
        if (mapping == null || !TryClampToPolygon(mapping, icon.position, out place, out _))
            return false;

        Transform map = MapTransformOrSelf();
        Vector3 world = map.TransformPoint(GetColmapPosition(place.lon, place.lat, place.alt));
        // Dropped under the map surface: bring it back up onto the surface.
        if (TryKeepAboveSurface(ref world))
            place.alt = ColmapToWgs84(map.InverseTransformPoint(world)).alt;

        AnchorPolygonIcon(icon, place);
        polygonIconPlaces[PolygonIconKey(mapping)] = place;
        return true;
    }

    // Called for a freshly spawned polygon icon.
    private void RestorePolygonIcon(Transform icon)
    {
        NodeMapping mapping = FindPolygonIconMapping(icon);
        if (mapping == null)
            return;

        string key = PolygonIconKey(mapping);
        Vector3Double remembered;
        if (!polygonIconPlaces.TryGetValue(key, out remembered))
            return;

        // The polygon changed and the place is now outside: back to the centre.
        if (!TryReadOuterRing(mapping, remembered.lat) ||
            !PolygonContainment.Contains(polygonRingBuffer, ToFlat(remembered.lon, remembered.lat, remembered.lat)))
        {
            polygonIconPlaces.Remove(key);
            return;
        }

        AnchorPolygonIcon(icon, remembered);
    }

    // Called after a corner of the polygon was moved and the icon was set to
    // the new centre: a remembered place that is still inside wins; otherwise
    // the icon takes the new centre.
    private void SettlePolygonIcon(Transform icon)
    {
        NodeMapping mapping = FindPolygonIconMapping(icon);
        if (mapping == null)
            return;

        string key = PolygonIconKey(mapping);
        Vector3Double remembered;
        if (polygonIconPlaces.TryGetValue(key, out remembered) && TryReadOuterRing(mapping, remembered.lat) &&
            PolygonContainment.Contains(polygonRingBuffer, ToFlat(remembered.lon, remembered.lat, remembered.lat)))
        {
            AnchorPolygonIcon(icon, remembered);
            return;
        }

        polygonIconPlaces.Remove(key);
        Vector3Double place;
        if (TryClampToPolygon(mapping, icon.position, out place, out _))
            AnchorPolygonIcon(icon, place);
    }

    private void AnchorPolygonIcon(Transform icon, Vector3Double place)
    {
        Transform map = MapTransformOrSelf();
        Vector3 local = GetColmapPosition(place.lon, place.lat, place.alt);
        icon.position = map.TransformPoint(local);

        FloatingIcon floating = icon.GetComponent<FloatingIcon>();
        if (floating != null)
            floating.localMapPoint = local;

        PointData data = icon.GetComponent<PointData>();
        if (data != null)
        {
            data.longitude = place.lon;
            data.latitude = place.lat;
            data.altitude = place.alt;
            RefreshLiveHeightAboveSurface(data, icon);
        }
    }

    private bool TryClampToPolygon(NodeMapping mapping, Vector3 worldPosition, out Vector3Double place, out bool moved)
    {
        moved = false;
        place = ColmapToWgs84(MapTransformOrSelf().InverseTransformPoint(worldPosition));
        if (!TryReadOuterRing(mapping, place.lat))
            return false;

        PolygonContainment.Point held = ToFlat(place.lon, place.lat, place.lat);
        PolygonContainment.Point inside;
        if (!PolygonContainment.TryClampInside(polygonRingBuffer, held, PolygonIconInset, out inside))
            return false;

        moved = inside.x != held.x || inside.y != held.y;
        if (moved)
        {
            place.lon = inside.x / LongitudeScale(place.lat);
            place.lat = inside.y;
        }
        return true;
    }

    // Fills polygonRingBuffer with the outer outline of the icon's polygon.
    private bool TryReadOuterRing(NodeMapping mapping, double referenceLatitude)
    {
        polygonRingBuffer.Clear();

        // A polygon icon is mapped to its polygon: a list of rings, outer ring first.
        JArray rings = mapping.coordArray;
        JArray outer = rings != null && rings.Count > 0 ? rings[0] as JArray : null;
        if (outer == null)
            return false;

        foreach (JToken token in outer)
        {
            JArray corner = token as JArray;
            if (corner == null || corner.Count < 2 || !TryGetDouble(corner[0], out double lon) || !TryGetDouble(corner[1], out double lat))
                continue;
            polygonRingBuffer.Add(ToFlat(lon, lat, referenceLatitude));
        }
        return polygonRingBuffer.Count >= 3;
    }

    // Degrees of longitude are shorter than degrees of latitude away from the
    // equator; scaling them makes "nearest" mean nearest on the ground.
    private static double LongitudeScale(double latitude)
    {
        return Math.Max(1e-6d, Math.Cos(latitude * Math.PI / 180d));
    }

    private static PolygonContainment.Point ToFlat(double lon, double lat, double referenceLatitude)
    {
        return new PolygonContainment.Point(lon * LongitudeScale(referenceLatitude), lat);
    }

    private NodeMapping FindPolygonIconMapping(Transform icon)
    {
        if (icon == null)
            return null;
        return nodeMappings.Find(m => m.node == icon && m.isCentroid);
    }

    // Feature id, plus the icon's position among the feature's icons for a multi-polygon.
    private string PolygonIconKey(NodeMapping mapping)
    {
        int ordinal = 0;
        foreach (NodeMapping other in nodeMappings)
        {
            if (other == mapping)
                break;
            if (other.isCentroid && ReferenceEquals(other.parentFeature, mapping.parentFeature))
                ordinal++;
        }

        string id = mapping.parentFeature?.id ?? mapping.parentFeature?.properties?.id ?? string.Empty;
        return id + "#" + ordinal;
    }

    private Transform MapTransformOrSelf()
    {
        return mapTransform != null ? mapTransform : transform;
    }
}
