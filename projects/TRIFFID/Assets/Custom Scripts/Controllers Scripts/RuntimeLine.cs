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

    public int NodeCount => _nodes.Count;

    public void Initialize(LineRenderer lineRenderer)
    {
        _lineRenderer = lineRenderer;
        MarkDirty();
    }

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
        foreach (var pair in _observers)
        {
            if (pair.Value != null)
                pair.Value.Changed -= MarkDirty;
        }

        _observers.Clear();
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
