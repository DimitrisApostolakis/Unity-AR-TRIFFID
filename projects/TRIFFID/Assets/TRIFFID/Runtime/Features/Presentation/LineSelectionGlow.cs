using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Selection cue for a whole line or polygon. While one of its vertices is the
// selected marker, a soft glow breathes along the entire outline and a
// polygon's interior brightens with it, so the selected feature is visible as
// one object rather than as a single dot. The vertex halos take the line's
// colour. The outline itself is never modified.
//
// Runs after RuntimeLine has synchronised the outline and before nothing
// depends on it; PolygonFill picks the boost up on its next frame.
[DefaultExecutionOrder(140)]
[DisallowMultipleComponent]
public class LineSelectionGlow : MonoBehaviour
{
    // Under the outline, above the polygon interior.
    private const int GlowSortingOrder = -6;

    private static Material glowMaterial;

    private LineRenderer outline;
    private RuntimeLine runtimeLine;
    private PolygonFill fill;
    private bool resolved;

    private LineRenderer glow;
    private Vector3[] buffer = new Vector3[16];
    private readonly List<PulseEffect> pulses = new List<PulseEffect>();
    private int cachedNodeCount = -1;
    private float time;
    private bool showing;

    public static void Attach(LineRenderer line)
    {
        // Editor checks spawn lines outside Play mode; never add scene objects there.
        if (line == null || !Application.isPlaying || !TriffidVisualSettings.Current.lineSelectionGlow)
            return;

        if (line.GetComponent<LineSelectionGlow>() == null)
            line.gameObject.AddComponent<LineSelectionGlow>();
    }

    private void Awake()
    {
        outline = GetComponent<LineRenderer>();
    }

    private void OnDisable()
    {
        Hide();
    }

    private void LateUpdate()
    {
        if (outline == null)
            return;

        if (!resolved)
        {
            // Added by the creator right after this component, in the same frame.
            resolved = true;
            runtimeLine = GetComponent<RuntimeLine>();
            fill = GetComponent<PolygonFill>();
        }

        if (!outline.enabled || outline.positionCount < 2 || !IsSelected())
        {
            Hide();
            return;
        }

        Show();
    }

    private bool IsSelected()
    {
        if (runtimeLine == null)
            return false;

        IReadOnlyList<Transform> nodes = runtimeLine.Nodes;
        if (nodes.Count != cachedNodeCount)
        {
            cachedNodeCount = nodes.Count;
            pulses.Clear();

            Color tint = GlowColor();
            for (int i = 0; i < nodes.Count; i++)
            {
                Transform node = nodes[i];
                if (node == null)
                    continue;

                PulseEffect pulse = node.GetComponent<PulseEffect>();
                if (pulse == null)
                    pulse = node.GetComponentInChildren<PulseEffect>(true);
                if (pulse == null)
                    continue;

                pulse.SetTint(tint);
                pulses.Add(pulse);
            }
        }

        for (int i = 0; i < pulses.Count; i++)
            if (pulses[i] != null && pulses[i].IsPulsing)
                return true;

        return false;
    }

    private Color GlowColor()
    {
        Color color = Color.Lerp(outline.startColor, Color.white, 0.3f);
        color.a = 1f;
        return color;
    }

    private void Show()
    {
        if (!EnsureGlow())
            return;

        TriffidVisualSettings settings = TriffidVisualSettings.Current;

        int count = outline.positionCount;
        if (buffer.Length < count)
            buffer = new Vector3[Mathf.NextPowerOfTwo(count)];

        count = outline.GetPositions(buffer);
        if (!outline.useWorldSpace)
        {
            Transform outlineTransform = outline.transform;
            for (int i = 0; i < count; i++)
                buffer[i] = outlineTransform.TransformPoint(buffer[i]);
        }

        glow.positionCount = count;
        glow.SetPositions(buffer);
        glow.loop = outline.loop;

        time += Time.unscaledDeltaTime;
        float breath = 0.5f - 0.5f * Mathf.Cos(time * Mathf.PI * 2f / Mathf.Max(0.2f, settings.lineGlowSeconds));

        float width = Mathf.Max(outline.startWidth, 1e-5f) * Mathf.Max(1f, settings.lineGlowWidth);
        glow.startWidth = width;
        glow.endWidth = width;

        Color color = GlowColor();
        color.a = settings.lineGlowAlpha * Mathf.Lerp(0.45f, 1f, breath);
        glow.startColor = color;
        glow.endColor = color;

        if (!glow.enabled)
            glow.enabled = true;

        if (fill != null)
            fill.SelectionBoost = settings.polygonSelectedFillBoost * breath;

        showing = true;
    }

    private void Hide()
    {
        if (!showing)
            return;

        showing = false;
        time = 0f;

        if (glow != null)
            glow.enabled = false;
        if (fill != null)
            fill.SelectionBoost = 0f;
    }

    private bool EnsureGlow()
    {
        if (glow != null)
            return true;

        if (glowMaterial == null)
        {
            // Needs vertex colours and alpha blending, like the polygon interior.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return false;

            glowMaterial = new Material(shader);
            glowMaterial.name = "Line Selection Glow (runtime)";
            glowMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        // World-space positions, so the parent's motion does not matter; as a
        // child it is hidden and destroyed together with its line.
        GameObject glowObject = new GameObject("SelectionGlow");
        glowObject.layer = gameObject.layer;
        glowObject.transform.SetParent(transform, false);

        glow = glowObject.AddComponent<LineRenderer>();
        glow.sharedMaterial = glowMaterial;
        glow.useWorldSpace = true;
        glow.alignment = LineAlignment.View;
        glow.textureMode = LineTextureMode.Stretch;
        glow.numCornerVertices = 6;
        glow.numCapVertices = 6;
        glow.shadowCastingMode = ShadowCastingMode.Off;
        glow.receiveShadows = false;
        glow.lightProbeUsage = LightProbeUsage.Off;
        glow.reflectionProbeUsage = ReflectionProbeUsage.Off;
        glow.sortingOrder = GlowSortingOrder;
        glow.enabled = false;
        return true;
    }
}
