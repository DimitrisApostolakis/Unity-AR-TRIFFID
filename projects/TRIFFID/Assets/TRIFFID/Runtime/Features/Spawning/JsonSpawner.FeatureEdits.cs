using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class JsonSpawner
{
    public void BeginFeatureEdit(Transform node)
    {
        if (node == null) return;
        Transform map = mapTransform != null ? mapTransform : transform;
        foreach (NodeMapping mapping in nodeMappings)
        {
            if (mapping.node != node || mapping.isCentroid || mapping.editInProgress) continue;
            PointData data = node.GetComponent<PointData>();
            if (data == null) continue;
            mapping.editInProgress = true;
            mapping.editStartParent = node.parent;
            mapping.editStartLocal = node.localPosition;
            mapping.editStartMapLocal = map.InverseTransformPoint(node.position);
            mapping.editStartGeographic = new Vector3Double { lon = data.longitude, lat = data.latitude, alt = data.altitude };
        }
    }

    public void CancelFeatureEdit(Transform node)
    {
        foreach (NodeMapping mapping in nodeMappings)
        {
            if (mapping.node != node || !mapping.editInProgress) continue;
            mapping.editInProgress = false;
            if (node.parent == mapping.editStartParent) node.localPosition = mapping.editStartLocal;
            else
            {
                Transform map = mapTransform != null ? mapTransform : transform;
                node.position = map.TransformPoint(mapping.editStartMapLocal);
            }
            SyncPointDataFromWgs(node, mapping.editStartGeographic);
        }
    }

    private bool CompleteFeatureEdit(Transform node)
    {
        if (node == null) return false;
        NodeMapping edited = nodeMappings.Find(m => m.node == node && !m.isCentroid && m.editInProgress);
        if (edited == null) return false;
        Transform map = mapTransform != null ? mapTransform : transform;
        bool unchanged = node.parent == edited.editStartParent
            ? node.localPosition.Equals(edited.editStartLocal)
            : map.InverseTransformPoint(node.position).Equals(edited.editStartMapLocal);
        Vector3Double original = edited.editStartGeographic;
        foreach (NodeMapping mapping in nodeMappings)
            if (mapping.node == node) mapping.editInProgress = false;
        if (unchanged)
        {
            SyncPointDataFromWgs(node, original);
            return false;
        }
        PointData data = node.GetComponent<PointData>();
        if (data == null) return false;
        // Preview values never enter feature data. Sample the release position
        // once here and commit only mappings belonging to this edited vertex.
        data.SetElevationUnresolved(false);
        FloatingIcon icon = node.GetComponent<FloatingIcon>();
        if (icon != null) icon.localMapPoint = map.InverseTransformPoint(node.position);
        RefreshLiveHeightAboveSurface(data, node);
        var vertices = new List<JArray>();
        foreach (NodeMapping mapping in nodeMappings)
            if (mapping.node == node && !mapping.isCentroid && ReferenceEquals(mapping.parentFeature, edited.parentFeature) &&
                mapping.coordArray != null && !vertices.Contains(mapping.coordArray)) vertices.Add(mapping.coordArray);
        string error;
        if (!GeoJsonVertexEdit.TryApply(edited.parentFeature, vertices, data.longitude, data.latitude, data.altitude, out error))
        {
            ReportValidationError(error);
            SyncPointDataFromWgs(node, original);
            Vector3 position = GetColmapPosition(original.lon, original.lat, original.alt);
            node.position = map.TransformPoint(position);
            if (icon != null) icon.localMapPoint = position;
            return false;
        }
        Feature feature = edited.parentFeature;
        if (feature.properties == null) feature.properties = new Properties();
        if (IsPointFeature(feature))
        {
            feature.properties.altitude_m = (float)data.altitude;
            StorePointHeightMetadata(feature, data);
        }
        else if (SupportsPerVertexHeight(feature))
        {
            foreach (JArray vertex in vertices)
            {
                feature.properties.heights_above_surface_m = GeoJsonVertexEdit.SetVertexValue(
                    feature.properties.heights_above_surface_m, feature.geometry.coordinates, vertex,
                    data.hasHeightAboveSurface ? new JValue(data.heightAboveSurface) : JValue.CreateNull());
                feature.properties.heightSources = GeoJsonVertexEdit.SetVertexValue(
                    feature.properties.heightSources, feature.geometry.coordinates, vertex, new JValue(data.heightSource));
                feature.properties.heightStatuses = GeoJsonVertexEdit.SetVertexValue(
                    feature.properties.heightStatuses, feature.geometry.coordinates, vertex, new JValue(data.heightStatus));
            }
        }
        if (edited.parentCentroid != null) RecalculateCentroidForNode(edited);
        MarkDataDirty();
        return true;
    }
}
