using UnityEngine;
using UnityEngine.Rendering;

// Ground reference for a floating POI icon.
//
// POI sprites are pivoted at their ground-contact point (pin tip, badge base,
// wheels), so the icon's transform position is the feature's location. The
// icon still bobs and billboards, so this component also draws a flat ring, a
// centre dot and a thin tether at the surface point below that anchor. It only
// reads the icon's state: FloatingIcon, PulseEffect and the saved coordinates
// are never written, and the visuals live outside the map hierarchy so map
// scans (clearing, line widths, experiment collectors) do not see them.
//
// Runs after FloatingIcon.LateUpdate so the tether meets the icon where it is
// drawn this frame.
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public class MarkerFootprint : MonoBehaviour
{
    private const int RingSegments = 48;
    private const int DotSegments = 12;

    // Ring, dot and tether sizes come from TriffidVisualSettings. They are
    // relative to the drawn icon height, so the footprint follows map scale.

    // Only for a sprite still pivoted at its centre: assume a pin whose tip
    // lies this far below. Sprites pivoted at their contact point need no guess.
    private const float LegacyPinTipPerIconHeight = 0.40f;
    private const float CentredPivotTolerance = 0.02f;

    // Relative to the ring radius.
    private const float SurfaceLift = 0.03f;
    private const float MinStemLength = 0.25f;

    private const float EmphasisPerSecond = 8f;

    // Below the pulse ring (-1) and the icon sprite (0): an icon always covers
    // its own footprint instead of being crossed by it.
    private const int WaveSortingOrder = -5;
    private const int WaveCount = 2;
    private const int RingSortingOrder = -4;
    private const int DotSortingOrder = -3;
    private const int StemSortingOrder = -2;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private enum ColorMode { Vertex, BaseColorProperty, ColorProperty }

    private static Transform container;
    private static Material fallbackMaterial;

    private TriffidVisualSettings settings;
    private JsonSpawner spawner;
    private FloatingIcon icon;
    private PointData data;
    private PulseEffect pulse;
    private SpriteRenderer iconSprite;

    private GameObject visualRoot;
    private Transform visualTransform;
    private LineRenderer ring;
    private LineRenderer dot;
    private LineRenderer stem;
    private LineRenderer[] waves;
    private Material lineMaterial;
    private float waveTime;
    private ColorMode colorMode;
    private MaterialPropertyBlock block;

    private float emphasis;
    private float held;
    private string accentClass;
    private Color accent = Color.white;

    private float appliedRadius = -1f;
    private float appliedEmphasis = -1f;
    private float appliedHeld = -1f;
    private bool appliedGrounded;
    private Color appliedAccent;
    private bool styleApplied;

    // The sprite pivot is the contact point, so the art sits above the origin.
    // Keep the grab/hover collider and the selection pulse on the drawn icon.
    // Absolute, so repeated calls and centre-pivoted sprites are both safe.
    public static void AlignToSpritePivot(GameObject marker)
    {
        if (marker == null)
            return;

        SpriteRenderer sprite = marker.GetComponent<SpriteRenderer>();
        if (sprite == null || sprite.sprite == null)
            return;

        Vector3 artCentre = sprite.sprite.bounds.center;

        BoxCollider box = marker.GetComponent<BoxCollider>();
        if (box != null)
            box.center = new Vector3(artCentre.x, artCentre.y, box.center.z);

        PulseEffect markerPulse = marker.GetComponent<PulseEffect>();
        if (markerPulse == null)
            markerPulse = marker.GetComponentInChildren<PulseEffect>(true);
        if (markerPulse != null)
            markerPulse.CenterOn(marker.transform.TransformPoint(new Vector3(artCentre.x, artCentre.y, 0f)));
    }

    public static void Attach(GameObject marker, JsonSpawner owner)
    {
        // Editor checks spawn markers outside Play mode; never add scene objects there.
        if (marker == null || !Application.isPlaying)
            return;

        MarkerFootprint footprint = marker.GetComponent<MarkerFootprint>();
        if (footprint == null)
            footprint = marker.AddComponent<MarkerFootprint>();

        footprint.Initialize(owner);
    }

    private void Initialize(JsonSpawner owner)
    {
        spawner = owner;
        settings = TriffidVisualSettings.Current;
        icon = GetComponent<FloatingIcon>();
        data = GetComponent<PointData>();
        iconSprite = GetComponent<SpriteRenderer>();
        pulse = GetComponent<PulseEffect>();
        if (pulse == null)
            pulse = GetComponentInChildren<PulseEffect>(true);

        if (icon == null || visualRoot != null)
            return;

        Material material = ResolveMaterial(owner);
        if (material == null)
            return;

        lineMaterial = material;
        colorMode = ResolveColorMode(material);
        block = new MaterialPropertyBlock();

        visualRoot = new GameObject("Footprint_" + gameObject.name);
        visualRoot.layer = gameObject.layer;
        visualTransform = visualRoot.transform;
        visualTransform.SetParent(GetContainer(), false);

        ring = CreateLine("Ring", material, RingSortingOrder, false);
        SetCircle(ring, RingSegments, 1f);

        dot = CreateLine("Dot", material, DotSortingOrder, false);
        SetCircle(dot, DotSegments, settings.footprintDotRadius);

        stem = CreateLine("Stem", material, StemSortingOrder, true);
        stem.positionCount = 2;

        visualRoot.SetActive(false);
    }

    private void OnEnable()
    {
        // Shown again by the next LateUpdate, once its pose is current.
        styleApplied = false;
    }

    private void OnDisable()
    {
        // Category filters deactivate the icon; its footprint leaves with it.
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (visualRoot != null)
            Destroy(visualRoot);
    }

    private void LateUpdate()
    {
        if (visualRoot == null)
            return;

        Transform map = icon != null ? icon.mainMap : null;
        if (map == null)
        {
            SetVisible(false);
            return;
        }

        Transform iconTransform = transform;
        bool isHeld = icon.IsBeingManipulated;

        // The anchor ignores the idle bob. While the icon is held it has no bob
        // and its live position is the anchor being edited.
        Vector3 anchor = isHeld ? iconTransform.position : map.TransformPoint(icon.localMapPoint);

        Vector3 up = map.up;
        float unitsPerMetre = 0f;
        bool hasVertical = spawner != null && spawner.TryGetMapVertical(out up, out unitsPerMetre);
        if (!hasVertical)
        {
            up = map.up;
            unitsPerMetre = 0f;
        }

        // Reuse the height the info panel reports, so both always agree.
        bool grounded = hasVertical && data != null && !data.elevationUnresolved &&
            data.hasHeightAboveSurface && IsFinite(data.heightAboveSurface);
        Vector3 ground = grounded ? anchor - up * ((float)data.heightAboveSurface * unitsPerMetre) : anchor;

        float iconHeight = Mathf.Abs(iconTransform.lossyScale.y);
        if (iconSprite != null && iconSprite.sprite != null)
            iconHeight *= iconSprite.sprite.bounds.size.y;

        if (!IsFinite(iconHeight) || iconHeight <= 1e-6f || !IsFinite(ground.x) || !IsFinite(ground.y) || !IsFinite(ground.z))
        {
            SetVisible(false);
            return;
        }

        UpdateEmphasis(isHeld);

        float radius = iconHeight * settings.footprintRingRadius * Mathf.Lerp(1f, settings.footprintFocusScale, Mathf.Max(emphasis, held));
        Vector3 tangent = Vector3.ProjectOnPlane(map.forward, up);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.ProjectOnPlane(map.right, up);

        Vector3 surfacePoint = ground + up * (radius * SurfaceLift);
        visualTransform.SetPositionAndRotation(surfacePoint, Quaternion.LookRotation(up, tangent));
        visualTransform.localScale = new Vector3(radius, radius, radius);

        UpdateAccent();
        ApplyStyle(radius, grounded);
        UpdateWaves(radius);

        // Without a known surface only the ring is shown: no exact point is claimed.
        if (dot.enabled != grounded)
            dot.enabled = grounded;

        Vector3 pinTip = iconTransform.position;
        if (IsCentrePivoted())
            pinTip -= iconTransform.up * (iconHeight * LegacyPinTipPerIconHeight);
        bool showStem = grounded && (pinTip - surfacePoint).sqrMagnitude > radius * radius * MinStemLength * MinStemLength;
        if (stem.enabled != showStem)
            stem.enabled = showStem;
        if (showStem)
        {
            stem.SetPosition(0, surfacePoint);
            stem.SetPosition(1, pinTip);
        }

        SetVisible(true);
    }

    private bool IsCentrePivoted()
    {
        if (iconSprite == null || iconSprite.sprite == null)
            return false;

        Bounds bounds = iconSprite.sprite.bounds;
        return Mathf.Abs(bounds.center.y) <= bounds.size.y * CentredPivotTolerance;
    }

    private void UpdateEmphasis(bool isHeld)
    {
        // Mirrors the panel's current marker through the existing pulse, so the
        // ring and the pulse can never disagree about what is selected.
        float emphasisTarget = pulse != null && pulse.IsPulsing ? 1f : 0f;
        float step = EmphasisPerSecond * Time.unscaledDeltaTime;
        emphasis = Mathf.MoveTowards(emphasis, emphasisTarget, step);
        held = Mathf.MoveTowards(held, isHeld ? 1f : 0f, step);
    }

    private void UpdateAccent()
    {
        string pointClass = data != null ? data.pointClass : null;
        if (ReferenceEquals(pointClass, accentClass))
            return;

        accentClass = pointClass;
        accent = SemanticPalette.ForClass(pointClass);
        if (pulse != null)
            pulse.SetTint(accent);
    }

    // Selection cue on the map itself: rings leave the footprint and fade as
    // they travel, like a sonar ping. Only the selected marker has them, and
    // they are created the first time that marker is selected.
    private void UpdateWaves(float radius)
    {
        bool active = settings.pulseGroundWaves && pulse != null && pulse.IsPulsing;
        if (!active)
        {
            if (waves != null && waveTime > 0f)
                foreach (LineRenderer wave in waves)
                    wave.enabled = false;

            waveTime = 0f;
            return;
        }

        if (waves == null)
        {
            waves = new LineRenderer[WaveCount];
            for (int i = 0; i < WaveCount; i++)
            {
                waves[i] = CreateLine("Wave", lineMaterial, WaveSortingOrder, false);
                SetCircle(waves[i], RingSegments, 1f);
                waves[i].enabled = false;
            }
        }

        waveTime += Time.unscaledDeltaTime;
        float period = Mathf.Max(0.2f, settings.pulseWaveSeconds);
        float reach = Mathf.Max(1f, settings.pulseWaveReach);
        Color tone = Color.Lerp(accent, Color.white, 0.25f);

        for (int i = 0; i < WaveCount; i++)
        {
            LineRenderer wave = waves[i];

            // Evenly staggered; a later wave waits for its turn instead of
            // appearing part-way out when the marker is first selected.
            float progress = waveTime / period - (float)i / WaveCount;
            if (progress < 0f)
            {
                wave.enabled = false;
                continue;
            }

            float t = Mathf.Repeat(progress, 1f);
            float remaining = 1f - t;
            float scale = Mathf.Lerp(1f, reach, 1f - remaining * remaining);

            wave.transform.localScale = new Vector3(scale, scale, 1f);
            wave.widthMultiplier = radius * settings.footprintRingWidth * Mathf.Lerp(1f, 0.4f, t);

            Color color = tone;
            color.a = remaining * remaining * 0.9f;
            SetColor(wave, color);
            wave.enabled = true;
        }
    }

    private void ApplyStyle(float radius, bool grounded)
    {
        // Line meshes are only touched when something visible changed.
        if (styleApplied && grounded == appliedGrounded && accent == appliedAccent &&
            Mathf.Abs(emphasis - appliedEmphasis) < 0.001f && Mathf.Abs(held - appliedHeld) < 0.001f &&
            Mathf.Abs(radius - appliedRadius) <= appliedRadius * 0.001f)
            return;

        styleApplied = true;
        appliedGrounded = grounded;
        appliedAccent = accent;
        appliedEmphasis = emphasis;
        appliedHeld = held;
        appliedRadius = radius;

        ring.widthMultiplier = radius * Mathf.Lerp(settings.footprintRingWidth, settings.footprintFocusRingWidth, Mathf.Max(emphasis, held));
        dot.widthMultiplier = radius * settings.footprintDotRadius * 2f;
        stem.widthMultiplier = radius * settings.footprintStemWidth;

        Color tone = Color.Lerp(accent, Color.white, Mathf.Clamp01(0.25f * emphasis + 0.55f * held));

        Color ringColor = tone;
        ringColor.a = grounded ? Mathf.Lerp(0.8f, 1f, Mathf.Max(emphasis, held)) : 0.4f;
        SetColor(ring, ringColor);

        Color dotColor = tone;
        dotColor.a = 1f;
        SetColor(dot, dotColor);

        Color stemColor = tone;
        stemColor.a = Mathf.Lerp(0.6f, 0.9f, Mathf.Max(emphasis, held));
        SetColor(stem, stemColor);
    }

    private void SetColor(LineRenderer line, Color color)
    {
        if (colorMode == ColorMode.Vertex)
        {
            line.startColor = color;
            line.endColor = color;
            return;
        }

        line.GetPropertyBlock(block);
        block.SetColor(colorMode == ColorMode.BaseColorProperty ? BaseColorId : ColorId, color);
        line.SetPropertyBlock(block);
    }

    private void SetVisible(bool visible)
    {
        if (visualRoot != null && visualRoot.activeSelf != visible)
            visualRoot.SetActive(visible);
    }

    private LineRenderer CreateLine(string lineName, Material material, int sortingOrder, bool worldSpace)
    {
        GameObject lineObject = new GameObject(lineName);
        lineObject.layer = gameObject.layer;
        lineObject.transform.SetParent(visualTransform, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = worldSpace;
        line.loop = !worldSpace;
        // Ring and dot lie flat in the map's horizontal plane; the tether faces the viewer.
        line.alignment = worldSpace ? LineAlignment.View : LineAlignment.TransformZ;
        line.textureMode = LineTextureMode.Stretch;
        line.numCornerVertices = 0;
        line.numCapVertices = 0;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
        line.sortingOrder = sortingOrder;
        line.startColor = Color.white;
        line.endColor = Color.white;
        return line;
    }

    private static void SetCircle(LineRenderer line, int segments, float radius)
    {
        line.positionCount = segments;
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
        }
    }

    private static Material ResolveMaterial(JsonSpawner owner)
    {
        // Same choice as the spawned GeoJSON lines, so footprints render wherever those do.
        if (owner != null && owner.lineMaterial != null)
            return owner.lineMaterial;

        if (fallbackMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            fallbackMaterial = new Material(shader);
            fallbackMaterial.name = "Marker Footprint (runtime)";
            fallbackMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        return fallbackMaterial;
    }

    private static ColorMode ResolveColorMode(Material material)
    {
        if (material.HasProperty(BaseColorId))
            return ColorMode.BaseColorProperty;

        Shader shader = material.shader;
        if (shader != null && shader.name.StartsWith("Sprites/", System.StringComparison.Ordinal))
            return ColorMode.Vertex;

        return material.HasProperty(ColorId) ? ColorMode.ColorProperty : ColorMode.Vertex;
    }

    private static Transform GetContainer()
    {
        if (container == null)
            container = new GameObject("Marker Footprints").transform;

        return container;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
