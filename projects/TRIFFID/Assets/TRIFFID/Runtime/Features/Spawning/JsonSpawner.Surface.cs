using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;

// Implementation of the existing JsonSpawner component. No additional scene component.
public partial class JsonSpawner
{
    private Vector3 GetWorldAltitudeAxis(Transform parentRef)
    {
        Vector3 up;
        float units;
        return transformData != null && transformData.colmap_to_enu != null &&
            GeographicSurfaceQuery.TryGetWorldVertical(parentRef, transformData.colmap_to_enu.R_rowmajor,
                transformData.colmap_to_enu.scale, out up, out units) ? up : Vector3.zero;
    }

    private bool TryGetSurfaceQueryScale(Transform parentRef, out Vector3 up, out float units)
    {
        up = Vector3.zero;
        units = 0f;
        return transformData != null && transformData.colmap_to_enu != null &&
            GeographicSurfaceQuery.TryGetWorldVertical(parentRef, transformData.colmap_to_enu.R_rowmajor,
                transformData.colmap_to_enu.scale, out up, out units);
    }

    private bool TrySnapToSurface(Vector3 approximateWorldPos, Transform parentRef, out Vector3 snappedWorldPos)
    {
        snappedWorldPos = approximateWorldPos;
        Vector3 axis;
        float units;
        if (!TryGetSurfaceQueryScale(parentRef, out axis, out units)) return false;
        bool activated = false;
        PrepareSurfaceForSnap(ref activated);
        try
        {
            float startMetres = Mathf.Max(0.25f, surfaceSnapRayStartOffset);
            float distanceMetres = Mathf.Max(0.5f, surfaceSnapRayDistance);
            float lift = Mathf.Max(0f, surfaceSnapOffset + surfaceSnapLift) * units;
            if (TryRaycastSurfaceFromPivot(approximateWorldPos, axis, startMetres * units, distanceMetres * units, lift, out snappedWorldPos)) return true;
            // Extend the search on the same geographic vertical. Never move the
            // query to the collider bounds centre or another horizontal location.
            return TryRaycastSurfaceFromPivot(approximateWorldPos, axis,
                Mathf.Max(startMetres * 8f, 250f) * units,
                Mathf.Max(distanceMetres * 16f, 2000f) * units, lift, out snappedWorldPos);
        }
        finally
        {
            if (activated && restoreSurfaceActiveStateAfterSnap && surfaceSnapTargetObject != null)
                surfaceSnapTargetObject.SetActive(false);
        }
    }

    public bool RefreshLiveHeightAboveSurface(PointData data, Transform sourceNode)
    {
        if (data == null || sourceNode == null || transformData == null) return false;
        FloatingIcon icon = sourceNode.GetComponent<FloatingIcon>();
        if (data.elevationUnresolved && (icon == null || !icon.IsBeingManipulated)) return false;
        data.SetElevationUnresolved(false);
        Transform parentRef = mapTransform != null ? mapTransform : transform;
        Vector3Double current = ColmapToWgs84(GetStableMapLocalPosition(sourceNode, parentRef));
        double oldHeight = data.heightAboveSurface, oldAltitude = data.altitude;
        bool hadHeight = data.hasHeightAboveSurface;
        string oldSource = data.heightSource, oldStatus = data.heightStatus;
        double calculated;
        if (TryCalculateHeightAboveSurface(sourceNode, parentRef, out calculated))
        {
            data.heightAboveSurface = calculated;
            data.hasHeightAboveSurface = true;
            data.heightSource = "mesh";
            data.heightStatus = calculated < 0d ? "below_surface" : "ok";
        }
        else if (hadHeight && IsFiniteNumber(oldHeight) && IsFiniteNumber(oldAltitude) && IsFiniteNumber(current.alt))
        {
            data.heightAboveSurface = oldHeight + current.alt - oldAltitude;
            data.heightSource = Math.Abs(current.alt - oldAltitude) > 1e-8 ? "altitude_delta" : "retained";
            data.heightStatus = "query_failed";
        }
        else
        {
            data.hasHeightAboveSurface = false;
            data.heightSource = "unavailable";
            data.heightStatus = "no_surface";
        }
        data.latitude = current.lat; data.longitude = current.lon; data.altitude = current.alt;
        return hadHeight != data.hasHeightAboveSurface || Math.Abs(oldHeight - data.heightAboveSurface) > 1e-4 ||
            oldSource != data.heightSource || oldStatus != data.heightStatus;
    }

    private bool CanQuerySurfaceHeight()
    {
        if (surfaceSnapTargetObject == null)
            return true;

        Collider targetCollider = ResolveSurfaceQueryCollider();
        return targetCollider != null &&
               targetCollider.enabled &&
               targetCollider.gameObject.activeInHierarchy;
    }

    private bool RefreshHeightAboveSurface(Transform pointTransform, Feature feature, PointData data, bool notifyDataChanged = true, bool persistMetadata = true)
    {
        if (pointTransform == null || data == null) return false;
        string before = feature == null || feature.properties == null ? null : JsonConvert.SerializeObject(feature.properties);
        Transform parentRef = mapTransform != null ? mapTransform : transform;
        double calculated;
        if (!data.elevationUnresolved && TryCalculateHeightAboveSurface(pointTransform, parentRef, out calculated))
        {
            data.heightAboveSurface = calculated; data.hasHeightAboveSurface = true;
            data.heightSource = "mesh"; data.heightStatus = calculated < 0d ? "below_surface" : "ok";
        }
        else if (!data.elevationUnresolved && feature != null && feature.properties != null &&
            feature.properties.height_above_surface_m.HasValue && IsFiniteNumber(feature.properties.height_above_surface_m.Value))
        {
            data.heightAboveSurface = feature.properties.height_above_surface_m.Value;
            data.hasHeightAboveSurface = true; data.heightSource = "retained"; data.heightStatus = "query_failed";
        }
        else
        {
            data.hasHeightAboveSurface = false; data.heightSource = "unavailable";
            data.heightStatus = data.elevationUnresolved ? "elevation_unresolved" : "no_surface";
        }
        if (feature != null && persistMetadata) StorePointHeightMetadata(feature, data);
        if (notifyDataChanged) data.NotifyDataChanged();
        return feature != null && before != JsonConvert.SerializeObject(feature.properties);
    }

    private static void StorePointHeightMetadata(Feature feature, PointData data)
    {
        if (feature.properties == null) feature.properties = new Properties();
        feature.properties.height_above_surface_m = data.hasHeightAboveSurface ? (float?)data.heightAboveSurface : null;
        feature.properties.heightSource = data.heightSource;
        feature.properties.heightStatus = data.heightStatus;
        feature.properties.elevationStatus = data.elevationUnresolved ? "unresolved" : "available";
    }

    private bool TryCalculateHeightAboveSurface(Transform pointTransform, Transform parentRef, out double heightAboveSurface)
    {
        heightAboveSurface = 0d;
        Vector3 axis;
        float units;
        if (pointTransform == null || !TryGetSurfaceQueryScale(parentRef, out axis, out units)) return false;
        Vector3 local = GetStableMapLocalPosition(pointTransform, parentRef);
        Vector3 world = parentRef.TransformPoint(local);
        bool activated = false;
        PrepareSurfaceForSnap(ref activated);
        try
        {
            Vector3 surface;
            float start = Mathf.Max(0.25f, surfaceSnapRayStartOffset);
            float distance = Mathf.Max(start + 0.5f, surfaceSnapRayDistance);
            if (!TryRaycastSurfaceBelow(world, axis, start * units, distance * units, out surface) &&
                !TryRaycastSurfaceBelow(world, axis, Mathf.Max(start * 8f, 250f) * units,
                    Mathf.Max(distance * 16f, 2000f) * units, out surface)) return false;
            Vector3Double markerWgs = ColmapToWgs84(local);
            Vector3Double surfaceWgs = ColmapToWgs84(parentRef.InverseTransformPoint(surface));
            heightAboveSurface = markerWgs.alt - surfaceWgs.alt;
            return IsFiniteNumber(heightAboveSurface);
        }
        finally
        {
            if (activated && restoreSurfaceActiveStateAfterSnap && surfaceSnapTargetObject != null)
                surfaceSnapTargetObject.SetActive(false);
        }
    }

    private bool TryRaycastSurfaceBelow(Vector3 pointWorldPosition, Vector3 castAxis, float rayStartOffset, float rayDistance, out Vector3 surfaceWorldPosition)
    {
        // Historical method name retained for callers. Above-marker hits are
        // deliberately accepted, so a below-surface marker remains negative.
        Collider target = surfaceSnapTargetObject == null ? null : ResolveSurfaceQueryCollider();
        if (surfaceSnapTargetObject != null && target == null) { surfaceWorldPosition = pointWorldPosition; return false; }
        return GeographicSurfaceQuery.TryHit(target, pointWorldPosition, castAxis, rayStartOffset, rayDistance, surfaceSnapMask, out surfaceWorldPosition);
    }

    public bool TryRaycastAnnotationSurface(Ray ray, float distance, LayerMask mask, out RaycastHit hit)
    {
        hit = default;
        bool activated = false;
        PrepareSurfaceForSnap(ref activated);
        try
        {
            Collider target = surfaceSnapTargetObject == null ? null : ResolveSurfaceQueryCollider();
            if (surfaceSnapTargetObject != null && target == null) return false;
            return GeographicSurfaceQuery.TryRaycast(target, ray, distance, mask, out hit);
        }
        finally
        {
            if (activated && restoreSurfaceActiveStateAfterSnap && surfaceSnapTargetObject != null)
                surfaceSnapTargetObject.SetActive(false);
        }
    }

    private Collider ResolveSurfaceQueryCollider()
    {
        if (surfaceQueryCollider != null)
            return surfaceQueryCollider;

        if (surfaceSnapTargetObject != null)
            surfaceQueryCollider = surfaceSnapTargetObject.GetComponentInChildren<Collider>(true);

        return surfaceQueryCollider;
    }

    private bool IsValidSurfaceCollider(Collider candidate)
    {
        if (candidate == null)
            return false;

        if (surfaceSnapTargetObject != null)
        {
            Transform targetTransform = surfaceSnapTargetObject.transform;
            return candidate.transform == targetTransform || candidate.transform.IsChildOf(targetTransform);
        }

        return candidate.GetComponentInParent<PointData>() == null;
    }

    private static bool IsPointFeature(Feature feature)
    {
        return string.Equals(feature?.geometry?.type, "Point", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsPerVertexHeight(Feature feature)
    {
        string geometryType = feature?.geometry?.type;
        return string.Equals(geometryType, "LineString", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(geometryType, "MultiLineString", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(geometryType, "Polygon", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(geometryType, "MultiPolygon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetStoredVertexHeight(Feature feature, JArray targetCoordinate, out double height)
    {
        height = 0d;
        if (!SupportsPerVertexHeight(feature) ||
            targetCoordinate == null ||
            feature.geometry?.coordinates == null ||
            feature.properties?.heights_above_surface_m == null)
        {
            return false;
        }

        return TryFindStoredVertexHeight(
            feature.geometry.coordinates,
            feature.properties.heights_above_surface_m,
            targetCoordinate,
            out height);
    }

    private static bool TryFindStoredVertexHeight(
        JToken coordinates,
        JToken heights,
        JArray targetCoordinate,
        out double height)
    {
        height = 0d;

        if (ReferenceEquals(coordinates, targetCoordinate))
        {
            if (!TryGetDouble(heights, out double parsedHeight) || !IsFiniteNumber(parsedHeight))
                return false;

            height = parsedHeight;
            return true;
        }

        if (!(coordinates is JArray coordinateArray) || !(heights is JArray heightArray))
            return false;

        int count = Math.Min(coordinateArray.Count, heightArray.Count);
        for (int i = 0; i < count; i++)
        {
            if (TryFindStoredVertexHeight(coordinateArray[i], heightArray[i], targetCoordinate, out height))
                return true;
        }

        return false;
    }

    private void UpdateFeatureHeightAboveSurfaceData(Feature feature)
    {
        if (!SupportsPerVertexHeight(feature) || feature.geometry?.coordinates == null)
            return;

        if (feature.properties == null)
            feature.properties = new Properties();

        Dictionary<JArray, PointData> heightByCoordinate = new Dictionary<JArray, PointData>();
        foreach (NodeMapping mapping in nodeMappings)
        {
            if (mapping == null || mapping.isCentroid ||
                !ReferenceEquals(mapping.parentFeature, feature) ||
                mapping.coordArray == null || mapping.node == null)
            {
                continue;
            }

            PointData pointData = mapping.node.GetComponent<PointData>();
            if (pointData != null)
                heightByCoordinate[mapping.coordArray] = pointData;
        }

        feature.properties.heights_above_surface_m =
            BuildVertexHeightTree(feature.geometry.coordinates, heightByCoordinate);
        feature.properties.height_above_surface_m = null;
        feature.properties.heightSources = BuildVertexMetadataTree(feature.geometry.coordinates, heightByCoordinate, true);
        feature.properties.heightStatuses = BuildVertexMetadataTree(feature.geometry.coordinates, heightByCoordinate, false);
    }

    private static JToken BuildVertexMetadataTree(JToken coordinates, Dictionary<JArray, PointData> values, bool source)
    {
        JArray array = coordinates as JArray;
        if (array == null) return JValue.CreateNull();
        if (IsCoordinateTuple(array))
        {
            PointData data;
            return values.TryGetValue(array, out data) && data != null
                ? new JValue(source ? data.heightSource : data.heightStatus)
                : new JValue(source ? "unavailable" : "no_surface");
        }
        JArray result = new JArray();
        foreach (JToken child in array) result.Add(BuildVertexMetadataTree(child, values, source));
        if (array.Count > 1 && JToken.DeepEquals(array[0], array[array.Count - 1]))
            result[result.Count - 1] = result[0].DeepClone();
        return result;
    }

    private static JToken BuildVertexHeightTree(
        JToken coordinates,
        Dictionary<JArray, PointData> heightByCoordinate)
    {
        if (!(coordinates is JArray coordinateArray))
            return JValue.CreateNull();

        if (IsCoordinateTuple(coordinateArray))
        {
            if (heightByCoordinate.TryGetValue(coordinateArray, out PointData pointData) &&
                pointData != null &&
                pointData.hasHeightAboveSurface &&
                IsFiniteNumber(pointData.heightAboveSurface))
            {
                return new JValue(pointData.heightAboveSurface);
            }

            return JValue.CreateNull();
        }

        JArray result = new JArray();
        foreach (JToken child in coordinateArray)
            result.Add(BuildVertexHeightTree(child, heightByCoordinate));

        // GeoJSON polygon rings repeat the first coordinate at the end. The
        // repeated vertex reuses the same scene node, so persist the same height.
        if (coordinateArray.Count > 1 &&
            coordinateArray[0] is JArray firstCoordinate &&
            coordinateArray[coordinateArray.Count - 1] is JArray lastCoordinate &&
            IsCoordinateTuple(firstCoordinate) &&
            IsCoordinateTuple(lastCoordinate) &&
            JToken.DeepEquals(firstCoordinate, lastCoordinate) &&
            result.Count == coordinateArray.Count)
        {
            result[result.Count - 1] = result[0].DeepClone();
        }

        return result;
    }

    private static bool IsCoordinateTuple(JArray coordinate)
    {
        return coordinate != null &&
               coordinate.Count >= 2 &&
               TryGetDouble(coordinate[0], out _) &&
               TryGetDouble(coordinate[1], out _);
    }

    private bool TryRaycastSurfaceFromPivot(Vector3 pivot, Vector3 castAxis, float rayStartOffset, float rayDistance, float snapLift, out Vector3 snappedWorldPos)
    {
        snappedWorldPos = pivot;
        Vector3 hit;
        if (!TryRaycastSurfaceBelow(pivot, castAxis, rayStartOffset, rayDistance, out hit)) return false;
        snappedWorldPos = hit + castAxis * snapLift;
        return true;
    }

    private void PrepareSurfaceForSnap(ref bool activatedSurfaceForSnap)
    {
        if (surfaceSnapTargetObject != null && autoActivateSurfaceForSnap && !surfaceSnapTargetObject.activeSelf)
        {
            surfaceSnapTargetObject.SetActive(true);
            activatedSurfaceForSnap = true;
        }

        if (surfaceSnapSyncBehaviour != null)
            surfaceSnapSyncBehaviour.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);
        else if (surfaceSnapTargetObject != null)
            surfaceSnapTargetObject.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);

        // The query synchronizes physics after restoring any disabled collider.
    }
}

