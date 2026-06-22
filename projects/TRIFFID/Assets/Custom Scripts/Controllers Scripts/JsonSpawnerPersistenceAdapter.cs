using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class JsonSpawnerPersistenceAdapter : MonoBehaviour, IAnnotationPersistenceService
{
    [SerializeField] private JsonSpawner jsonSpawner;
    private bool missingSpawnerWarningLogged;

    public void Inject(JsonSpawner injectedJsonSpawner)
    {
        jsonSpawner = injectedJsonSpawner;
        if (jsonSpawner != null)
            missingSpawnerWarningLogged = false;
    }

    public string SavePoint(GameObject pointObject, string pointClass)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("SavePoint");
            return string.Empty;
        }

        if (pointObject == null)
            return string.Empty;

        return jsonSpawner.AddPointFeatureAndSave(pointObject, pointClass);
    }

    public string SaveLine(IReadOnlyList<Vector3> worldPositions, string lineClass, Color color)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("SaveLine");
            return string.Empty;
        }

        if (worldPositions == null || worldPositions.Count < 2)
            return string.Empty;

        List<Vector3> positions = new List<Vector3>(worldPositions.Count);
        for (int i = 0; i < worldPositions.Count; i++)
            positions.Add(worldPositions[i]);

        return jsonSpawner.AddLineFeatureAndSave(positions, lineClass, color);
    }

    public string SavePolygon(IReadOnlyList<Vector3> worldPositions, string polygonClass, string polygonCategory, Color color)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("SavePolygon");
            return string.Empty;
        }

        if (worldPositions == null || worldPositions.Count < 3)
            return string.Empty;

        List<Vector3> positions = new List<Vector3>(worldPositions.Count);
        for (int i = 0; i < worldPositions.Count; i++)
            positions.Add(worldPositions[i]);

        return jsonSpawner.AddPolygonFeatureAndSave(positions, polygonClass, polygonCategory, color);
    }

    public bool DeleteFeatureByNode(Transform node, bool requireDrawMode = false)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("DeleteFeatureByNode");
            return false;
        }

        if (node == null)
            return false;

        return jsonSpawner.DeleteFeatureByNode(node, requireDrawMode);
    }

    public void RegisterRuntimeLineNodes(string featureId, List<Transform> nodes)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("RegisterRuntimeLineNodes");
            return;
        }

        if (string.IsNullOrEmpty(featureId) || nodes == null)
            return;

        jsonSpawner.RegisterRuntimeLineNodes(featureId, nodes);
    }

    public void RegisterRuntimePolygonNodes(string featureId, List<Transform> nodes)
    {
        if (jsonSpawner == null)
        {
            LogMissingSpawnerOnce("RegisterRuntimePolygonNodes");
            return;
        }

        if (string.IsNullOrEmpty(featureId) || nodes == null)
            return;

        jsonSpawner.RegisterRuntimePolygonNodes(featureId, nodes);
    }

    private void LogMissingSpawnerOnce(string operation)
    {
        if (missingSpawnerWarningLogged)
            return;

        Debug.LogWarning($"[JsonSpawnerPersistenceAdapter] {operation} cannot run because JsonSpawner is unavailable.", this);
        missingSpawnerWarningLogged = true;
    }
}
