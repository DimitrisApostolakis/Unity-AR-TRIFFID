using System;
using System.Collections.Generic;
using UnityEngine;

public enum ClassGroup
{
    Neutral,
    ActiveHazard,
    Obstacle,
    Life,
    Asset
}

[Serializable]
public class ClassStyle
{
    [Tooltip("Class name as it appears in the GeoJSON and on the menus. Matching ignores case.")]
    public string className;
    [Tooltip("Colour group used for this class on the map, the info panel and the menus.")]
    public ClassGroup group;

    public ClassStyle() { }

    public ClassStyle(string className, ClassGroup group)
    {
        this.className = className;
        this.group = group;
    }
}

// The one place to tune how annotations and menus look. Every value here was
// previously a constant inside a presentation script; the defaults reproduce
// that behaviour exactly, so the asset is optional.
//
// The asset at Assets/TRIFFID/Resources/TriffidVisualSettings.asset is loaded
// once per Play session. Values are read when a marker, polygon or menu is set
// up, so change them in Edit mode or restart Play to see the result.
[CreateAssetMenu(fileName = ResourceName, menuName = "TRIFFID/Visual Settings")]
public class TriffidVisualSettings : ScriptableObject
{
    public const string ResourceName = "TriffidVisualSettings";

    [Header("Features")]
    [Tooltip("Translucent interior for closed polygons.")]
    public bool polygonFills = true;
    [Tooltip("Icon-above, caption-below layout with a corner checkbox for the filter and spawn menus, and the Previous / Next / Delete order of the info panel.")]
    public bool menuCellLayout = true;
    [Tooltip("Tints filter, spawn and Safe/Unsafe captions with their group colour.")]
    public bool colorMenuCaptions = true;
    [Tooltip("Class-coloured title, confidence-coloured bar and icon settle on the info panel.")]
    public bool infoPanelStyling = true;
    [Tooltip("Replaces the generic speech hint with each button's own caption, for example Say 'Fire'.")]
    public bool nameSpeechHints = true;

    [Header("Classes")]
    [Tooltip("Which colour group each class belongs to. A class not listed here is Neutral. Prefabs and panel icons for a class are assigned on JsonSpawner, XRControllerLogger and PointInfoPanel; the Control Center shows them beside this list.")]
    public List<ClassStyle> classes = new List<ClassStyle>
    {
        new ClassStyle("fire", ClassGroup.ActiveHazard),
        new ClassStyle("smoke", ClassGroup.ActiveHazard),
        new ClassStyle("citizen", ClassGroup.Life),
        new ClassStyle("animal", ClassGroup.Life),
        new ClassStyle("ugv", ClassGroup.Asset),
        new ClassStyle("hole", ClassGroup.Obstacle),
        new ClassStyle("mud", ClassGroup.Obstacle),
        new ClassStyle("water", ClassGroup.Obstacle),
        new ClassStyle("debris", ClassGroup.Obstacle),
        new ClassStyle("barrel", ClassGroup.Obstacle),
        new ClassStyle("barrier", ClassGroup.Obstacle),
    };

    [Header("Group Colours")]
    [Tooltip("Fire and smoke.")]
    public Color activeHazard = new Color(1.00f, 0.36f, 0.30f);
    [Tooltip("Hole, mud, water, debris, barrel and barrier.")]
    public Color obstacle = new Color(1.00f, 0.77f, 0.24f);
    [Tooltip("Citizen, animal and Safe zones.")]
    public Color life = new Color(0.29f, 0.87f, 0.50f);
    [Tooltip("The UGV.")]
    public Color asset = new Color(0.22f, 0.88f, 1.00f);
    [Tooltip("Any other class.")]
    public Color neutral = new Color(0.90f, 0.95f, 0.96f);

    [Header("Mission Planner")]
    [Tooltip("Line colour of a Responders route (GeoJSON class responder_path), drawn or loaded.")]
    public Color responderPathColor = new Color(1.00f, 0.55f, 0.10f);
    [Tooltip("Line colour of a UGV route (GeoJSON class ugv_path), drawn or loaded.")]
    public Color ugvPathColor = new Color(0.22f, 0.88f, 1.00f);

    [Header("Marker Footprint (relative sizes)")]
    [Min(0.01f)]
    [Tooltip("Ring radius as a fraction of the drawn icon height.")]
    public float footprintRingRadius = 0.22f;
    [Min(0f)]
    [Tooltip("Ring line width as a fraction of the ring radius.")]
    public float footprintRingWidth = 0.08f;
    [Min(0f)]
    [Tooltip("Ring line width while the marker is selected or held.")]
    public float footprintFocusRingWidth = 0.14f;
    [Min(0f)]
    [Tooltip("Centre dot radius as a fraction of the ring radius.")]
    public float footprintDotRadius = 0.10f;
    [Min(0f)]
    [Tooltip("Tether width as a fraction of the ring radius.")]
    public float footprintStemWidth = 0.06f;
    [Min(1f)]
    [Tooltip("Ring size multiplier while the marker is selected or held.")]
    public float footprintFocusScale = 1.12f;

    [Header("Icon Motion")]
    [Tooltip("Off: every icon keeps the bob values saved on its own prefab. On: the two values below are applied to every icon when it is spawned.")]
    public bool overrideIconBob = false;
    [Min(0f)]
    [Tooltip("Vertical travel of the idle bob, in world units.")]
    public float iconBobAmplitude = 0.1f;
    [Min(0f)]
    [Tooltip("Bob cycles per second.")]
    public float iconBobFrequency = 0.2f;

    [Header("Selection Pulse")]
    [Tooltip("Rings that ripple outward on the map surface from the selected marker's footprint.")]
    public bool pulseGroundWaves = true;
    [Min(0.2f)]
    [Tooltip("Seconds for one ground wave to travel from the footprint ring to its full reach.")]
    public float pulseWaveSeconds = 1.6f;
    [Min(1f)]
    [Tooltip("How far a ground wave travels, as a multiple of the footprint ring radius.")]
    public float pulseWaveReach = 3f;
    [Range(0f, 1f)]
    [Tooltip("Peak opacity of the soft halo behind the selected icon. Zero hides the halo.")]
    public float pulseHaloAlpha = 0.45f;
    [Tooltip("Off: every halo keeps the duration and size saved on its own prefab. On: the two values below are used for every halo.")]
    public bool overrideHalo = false;
    [Min(0.05f)]
    [Tooltip("Seconds for one halo expansion.")]
    public float haloSeconds = 1.4f;
    [Min(1f)]
    [Tooltip("Size the halo grows to, relative to its starting size.")]
    public float haloMaxScale = 75f;

    [Header("Selected Line or Polygon")]
    [Tooltip("A soft glow breathes along the whole line or polygon while one of its vertices is selected.")]
    public bool lineSelectionGlow = true;
    [Min(1f)]
    [Tooltip("Glow width as a multiple of the line width.")]
    public float lineGlowWidth = 3.5f;
    [Range(0f, 1f)]
    [Tooltip("Peak opacity of the glow.")]
    public float lineGlowAlpha = 0.45f;
    [Min(0.2f)]
    [Tooltip("Seconds for one glow breath.")]
    public float lineGlowSeconds = 1.6f;
    [Range(0f, 1f)]
    [Tooltip("Extra interior opacity of a selected polygon at the peak of a breath.")]
    public float polygonSelectedFillBoost = 0.15f;

    [Header("Polygons and Lines")]
    [Range(0f, 1f)]
    [Tooltip("Opacity of the polygon interior. The outline keeps its own colour.")]
    public float polygonFillAlpha = 0.2f;
    [Range(0, 12)]
    [Tooltip("Rounding of line joins and ends. Zero gives square corners.")]
    public int lineCornerVertices = 4;

    [Header("Menu Cells (canvas units, cell is 32 x 32)")]
    public Vector2 cellIconSize = new Vector2(20f, 20f);
    public Vector2 cellIconPosition = new Vector2(0f, 3.5f);
    public Vector2 cellCaptionPosition = new Vector2(0f, -11.5f);
    public Vector2 cellCheckboxPosition = new Vector2(10.5f, 10.5f);
    [Range(0.1f, 1f)]
    public float cellCheckboxScale = 0.5f;
    [Range(0f, 1f)]
    [Tooltip("Icon opacity for a class that is switched off in the filter menu.")]
    public float cellOffIconAlpha = 0.35f;
    [Range(0f, 1f)]
    [Tooltip("Caption opacity for a class that is switched off in the filter menu.")]
    public float cellOffCaptionAlpha = 0.6f;

    public Color ColorOf(ClassGroup group)
    {
        switch (group)
        {
            case ClassGroup.ActiveHazard: return activeHazard;
            case ClassGroup.Obstacle: return obstacle;
            case ClassGroup.Life: return life;
            case ClassGroup.Asset: return asset;
            default: return neutral;
        }
    }

    private static TriffidVisualSettings current;

    public static TriffidVisualSettings Current
    {
        get
        {
            if (current == null)
                current = Resources.Load<TriffidVisualSettings>(ResourceName);

            // No asset: run on the built-in defaults instead of failing.
            if (current == null)
            {
                current = CreateInstance<TriffidVisualSettings>();
                current.hideFlags = HideFlags.HideAndDontSave;
            }

            return current;
        }
    }
}
