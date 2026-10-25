using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class RuntimeLine : MonoBehaviour
{
    private sealed class NodeTransformObserver : MonoBehaviour
    {
        public event Action Changed;

        private void LateUpdate()
        {
            if (!transform.hasChanged)
                return;

            transform.hasChanged = false;
            Changed?.Invoke();
        }
    }

    private readonly List<Transform> _nodes = new List<Transform>();
    private readonly Dictionary<Transform, NodeTransformObserver> _observers = new Dictionary<Transform, NodeTransformObserver>();
    private LineRenderer _lineRenderer;
    private bool _dirty;
    private bool _ownsNodes;
    private Material _ownedMaterial;

    public int NodeCount => _nodes.Count;
    public string FeatureId { get; private set; }
    public bool IsDraft => _ownsNodes && string.IsNullOrEmpty(FeatureId);

    public void Initialize(LineRenderer lineRenderer, bool ownsNodes = false)
    {
        _lineRenderer = lineRenderer;
        _ownsNodes = ownsNodes;
        _ownedMaterial = ownsNodes && lineRenderer != null ? lineRenderer.sharedMaterial : null;
        MarkDirty();
    }

    public void BindToFeature(string featureId) { FeatureId = featureId; }

    public void AddNode(Transform node)
    {
        if (node == null || _nodes.Contains(node))
            return;

        _nodes.Add(node);
        SubscribeToNode(node);
        MarkDirty();
        RefreshNow();
    }

    public void RemoveNode(Transform node)
    {
        if (node == null)
            return;

        if (_nodes.Remove(node))
        {
            UnsubscribeFromNode(node);
            MarkDirty();
            RefreshNow();
        }
    }

    public void RefreshNow()
    {
        SyncLineRenderer();
        _dirty = false;
    }

    private void LateUpdate()
    {
        if (!_dirty)
            return;

        SyncLineRenderer();
        _dirty = false;
    }

    private void OnDestroy()
    {
        ReleaseOwnedObjects();
    }

    // Draft vertices are siblings of their stroke, not children. Their owner
    // must release them too, otherwise deleting a stroke leaves orphan dots.
    public void ReleaseOwnedObjects()
    {
        foreach (var pair in _observers)
        {
            if (pair.Value != null)
                pair.Value.Changed -= MarkDirty;
        }

        _observers.Clear();
        if (_ownsNodes)
            foreach (Transform node in _nodes)
                if (node != null) DestroyOwned(node.gameObject);
        _nodes.Clear();
        if (_ownedMaterial != null) DestroyOwned(_ownedMaterial);
        _ownedMaterial = null;
    }

    private static void DestroyOwned(UnityEngine.Object value)
    {
        if (Application.isPlaying) Destroy(value);
        else DestroyImmediate(value);
    }

    private void SubscribeToNode(Transform node)
    {
        NodeTransformObserver observer = node.GetComponent<NodeTransformObserver>();
        if (observer == null)
            observer = node.gameObject.AddComponent<NodeTransformObserver>();

        observer.Changed -= MarkDirty;
        observer.Changed += MarkDirty;
        _observers[node] = observer;
    }

    private void UnsubscribeFromNode(Transform node)
    {
        if (!_observers.TryGetValue(node, out NodeTransformObserver observer))
            return;

        if (observer != null)
            observer.Changed -= MarkDirty;

        _observers.Remove(node);
    }

    private void SyncLineRenderer()
    {
        if (_lineRenderer == null)
            return;

        CompactNullNodes();

        _lineRenderer.positionCount = _nodes.Count;
        for (int i = 0; i < _nodes.Count; i++)
        {
            Transform node = _nodes[i];
            if (node != null)
                _lineRenderer.SetPosition(i, node.position);
        }
    }

    private void CompactNullNodes()
    {
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i] != null)
                continue;

            _nodes.RemoveAt(i);
        }
    }

    private void MarkDirty() => _dirty = true;
}

