using UnityEngine;
using System;
using System.Collections.Generic;

// The editor regression runner also creates and destroys markers outside Play
// mode. Receive lifecycle callbacks there so temporary materials are released.
[ExecuteAlways]
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
    private sealed class ElevationMaterial
    {
        public Material original;
        public Material highlight;
    }
    private readonly Dictionary<Renderer, ElevationMaterial> elevationMaterials = new Dictionary<Renderer, ElevationMaterial>();

    public void SetElevationUnresolved(bool unresolved)
    {
        elevationUnresolved = unresolved;
        if (!unresolved || !isActiveAndEnabled)
        {
            ReleaseElevationMaterials();
            return;
        }
        if (elevationMaterials.Count != 0) return;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            Material original = renderer.sharedMaterial;
            if (original == null) continue;
            // Own the temporary copy explicitly. Accessing renderer.material
            // would implicitly instantiate a copy and warns in Edit mode.
            Material highlight = new Material(original);
            highlight.hideFlags = HideFlags.HideAndDontSave;
            highlight.color = new Color(1f, 0.25f, 1f);
            elevationMaterials[renderer] = new ElevationMaterial { original = original, highlight = highlight };
            renderer.sharedMaterial = highlight;
        }
    }

    private void OnEnable() { SetElevationUnresolved(elevationUnresolved); }
    // OnDisable also runs before script reload and when a marker is hidden.
    // Retain the unresolved flag so OnEnable can restore the highlight.
    private void OnDisable() { ReleaseElevationMaterials(); }
    private void OnDestroy() { ReleaseElevationMaterials(); }

    private void ReleaseElevationMaterials()
    {
        foreach (KeyValuePair<Renderer, ElevationMaterial> saved in elevationMaterials)
        {
            ElevationMaterial materials = saved.Value;
            if (saved.Key != null && saved.Key.sharedMaterial == materials.highlight)
                saved.Key.sharedMaterial = materials.original;
            if (materials.highlight == null) continue;
            if (Application.isPlaying) Destroy(materials.highlight);
            else DestroyImmediate(materials.highlight);
        }
        elevationMaterials.Clear();
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

