using System;
using UnityEngine;

public static class GeographicSurfaceQuery
{
    // R maps COLMAP to ENU and scale is ENU metres per COLMAP unit. Transform
    // the displacement of one ENU metre, including the whole display hierarchy.
    public static bool TryGetWorldVertical(Transform map, float[] rotation, float scale,
        out Vector3 up, out float worldUnitsPerMetre)
    {
        up = Vector3.zero;
        worldUnitsPerMetre = 0f;
        if (map == null || rotation == null || rotation.Length < 9 || !Finite(scale) || scale <= 0f) return false;
        Vector3 oneMetre = new Vector3(rotation[6], rotation[7], rotation[8]) / scale;
        Vector3 displacement = map.localToWorldMatrix.MultiplyVector(oneMetre);
        float length = displacement.magnitude;
        if (!Finite(length) || length <= 1e-12f || !Finite(map.localToWorldMatrix.determinant) ||
            Math.Abs(map.localToWorldMatrix.determinant) <= 1e-20f) return false;
        up = displacement / length;
        worldUnitsPerMetre = length;
        return Finite(up.x) && Finite(up.y) && Finite(up.z);
    }

    public static bool TryHit(Collider target, Vector3 marker, Vector3 up,
        float startDistance, float rayDistance, LayerMask mask, out Vector3 surface)
    {
        surface = marker;
        if (up.sqrMagnitude < 0.99f || !Finite(startDistance) || !Finite(rayDistance) || rayDistance <= 0f) return false;
        // The selected reference is the first configured surface seen from ENU
        // above. A hit above the marker is valid and yields a negative offset.
        if (Cast(target, marker + up * startDistance, -up, rayDistance, mask, out surface)) return true;
        return Cast(target, marker - up * startDistance, up, rayDistance, mask, out surface);
    }

    private static bool Cast(Collider target, Vector3 origin, Vector3 direction, float distance, LayerMask mask, out Vector3 surface)
    {
        surface = origin;
        Ray ray = new Ray(origin, direction);
        if (target != null)
        {
            if (!target.enabled || !target.gameObject.activeInHierarchy) return false;
            RaycastHit hit;
            if (!target.Raycast(ray, out hit, distance)) return false;
            surface = hit.point;
            return true;
        }
        RaycastHit[] hits = Physics.RaycastAll(ray, distance, mask, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.GetComponentInParent<PointData>() != null) continue;
            surface = hit.point;
            return true;
        }
        return false;
    }

    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
