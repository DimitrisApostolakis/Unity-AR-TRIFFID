using UnityEngine;
using System;
using System.Collections.Generic;

public class PointData : MonoBehaviour
{
    public string pointClass = "-";
    public string pointID    = "-";
    public string category   = "-";
    public string source     = "-";
    public string behavior   = "-";
    public string lineColorHex = "";
    public double latitude   = 0.0;
    public double longitude  = 0.0;
    public double altitude   = 0.0;
    public double heightAboveSurface = 0.0;
    public bool hasHeightAboveSurface = false;
    public string heightSource = "unavailable";
    public string heightStatus = "no_surface";
    public bool elevationUnresolved;
    public string syncStatus = "";
    public float  confidence = 0f;
    private readonly Dictionary<Renderer, Color> resolvedColors = new Dictionary<Renderer, Color>();

    public void SetElevationUnresolved(bool unresolved)
    {
        if (unresolved && !elevationUnresolved)
        {
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterial == null) continue;
                resolvedColors[renderer] = renderer.material.color;
                renderer.material.color = new Color(1f, 0.25f, 1f);
            }
        }
        else if (!unresolved && elevationUnresolved)
        {
            foreach (KeyValuePair<Renderer, Color> saved in resolvedColors)
                if (saved.Key != null && saved.Key.sharedMaterial != null) saved.Key.material.color = saved.Value;
            resolvedColors.Clear();
        }
        elevationUnresolved = unresolved;
    }

    public Action OnDataChanged;

    public void OnPointSelected()
    {
        MarkerEventManager.RaiseMarkerSelected(this);
    }

    public void NotifyDataChanged()
    {
        OnDataChanged?.Invoke();
    }
}

