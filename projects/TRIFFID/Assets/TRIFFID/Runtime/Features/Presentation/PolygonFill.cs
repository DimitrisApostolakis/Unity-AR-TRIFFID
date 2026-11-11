using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Translucent interior for a closed outline, so a zone reads as an area rather
// than as a loop of line. It only reads the LineRenderer it sits beside: the
// outline, its nodes and the saved geometry are never written.
//
// The mesh is kept in world space under a scene-root container. Polygon lines
// can be parented to a bobbing, billboarded centroid icon, and a child mesh
// would inherit that motion. The mesh is rebuilt only when the outline's world
// positions or colour change.
//
// Runs after RuntimeLine.LateUpdate has synchronised the outline this frame.
[DefaultExecutionOrder(150)]
[DisallowMultipleComponent]
public class PolygonFill : MonoBehaviour
{
    private const float DuplicateDistanceSqr = 1e-12f;

    // Under outlines, footprints and icons.
    private const int FillSortingOrder = -10;

    private static Transform container;
    private static Material fillMaterial;
    private static bool missingShaderLogged;

    private LineRenderer outline;
    private GameObject fillObject;
    private MeshRenderer fillRenderer;
    private Mesh mesh;

    private Vector3[] buffer = new Vector3[16];
    private readonly List<Vector3> ring = new List<Vector3>();
    private readonly List<Vector3> applied = new List<Vector3>();
    private readonly List<Vector2> flat = new List<Vector2>();
    private readonly List<int> remaining = new List<int>();
    private readonly List<int> triangles = new List<int>();
    private readonly List<Color> colors = new List<Color>();
    private Color appliedColor;

    // Extra interior opacity while the polygon is selected; set by LineSelectionGlow.
    public float SelectionBoost { get; set; }

    // Rounded joins and ends for any annotation line, open or closed.
    public static void StyleOutline(LineRenderer line)
    {
        if (line == null)
            return;

        int rounding = Mathf.Max(0, TriffidVisualSettings.Current.lineCornerVertices);
        line.numCornerVertices = rounding;
        line.numCapVertices = rounding;
    }

    public static void Attach(LineRenderer line)
    {
        // Editor checks spawn lines outside Play mode; never add scene objects there.
        if (line == null || !Application.isPlaying || !TriffidVisualSettings.Current.polygonFills)
            return;

        if (line.GetComponent<PolygonFill>() == null)
            line.gameObject.AddComponent<PolygonFill>();
    }

    private void Awake()
    {
        outline = GetComponent<LineRenderer>();
    }

    private void OnDisable()
    {
        // Category filters deactivate the outline; its fill leaves with it.
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (fillObject != null)
            Destroy(fillObject);
        if (mesh != null)
            Destroy(mesh);
    }

    private void LateUpdate()
    {
        if (outline == null || !outline.enabled || !outline.loop || outline.positionCount < 3)
        {
            SetVisible(false);
            return;
        }

        if (!ReadRing() || !EnsureFill())
        {
            SetVisible(false);
            return;
        }

        Color color = outline.startColor;
        color.a = Mathf.Clamp01(TriffidVisualSettings.Current.polygonFillAlpha + SelectionBoost);

        if (!MatchesApplied())
        {
            if (!Rebuild(color))
            {
                SetVisible(false);
                return;
            }
        }
        else if (color != appliedColor)
        {
            // Same shape, new tint: no need to triangulate again.
            for (int i = 0; i < colors.Count; i++)
                colors[i] = color;
            mesh.SetColors(colors);
            appliedColor = color;
        }

        SetVisible(true);
    }

    private bool ReadRing()
    {
        int count = outline.positionCount;
        if (buffer.Length < count)
            buffer = new Vector3[Mathf.NextPowerOfTwo(count)];

        count = outline.GetPositions(buffer);
        bool local = !outline.useWorldSpace;
        Transform outlineTransform = outline.transform;

        ring.Clear();
        for (int i = 0; i < count; i++)
        {
            Vector3 point = local ? outlineTransform.TransformPoint(buffer[i]) : buffer[i];
            if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsNaN(point.z) ||
                float.IsInfinity(point.x) || float.IsInfinity(point.y) || float.IsInfinity(point.z))
                return false;

            if (ring.Count > 0 && (point - ring[ring.Count - 1]).sqrMagnitude < DuplicateDistanceSqr)
                continue;

            ring.Add(point);
        }

        // A ring that repeats its first point at the end is already closed by the loop.
        if (ring.Count > 1 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < DuplicateDistanceSqr)
            ring.RemoveAt(ring.Count - 1);

        return ring.Count >= 3;
    }

    private bool MatchesApplied()
    {
        if (applied.Count != ring.Count)
            return false;

        for (int i = 0; i < ring.Count; i++)
            if (applied[i] != ring[i])
                return false;

        return true;
    }

    private bool Rebuild(Color color)
    {
        int count = ring.Count;

        // Newell's method: a stable normal even when the vertices are not coplanar.
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % count];
            normal.x += (a.y - b.y) * (a.z + b.z);
            normal.y += (a.z - b.z) * (a.x + b.x);
            normal.z += (a.x - b.x) * (a.y + b.y);
        }

        if (normal.sqrMagnitude < 1e-20f)
            return false;

        normal.Normalize();
        Vector3 axisX = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 axisY = Vector3.Cross(normal, axisX);

        flat.Clear();
        float doubleArea = 0f;
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = ring[i] - ring[0];
            flat.Add(new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY)));
        }
        for (int i = 0; i < count; i++)
        {
            Vector2 a = flat[i];
            Vector2 b = flat[(i + 1) % count];
            doubleArea += a.x * b.y - b.x * a.y;
        }

        if (Mathf.Abs(doubleArea) < 1e-12f)
            return false;

        // Ear clipping expects a counter-clockwise ring in the projected plane.
        remaining.Clear();
        if (doubleArea > 0f)
            for (int i = 0; i < count; i++) remaining.Add(i);
        else
            for (int i = count - 1; i >= 0; i--) remaining.Add(i);

        triangles.Clear();
        int guard = count * count + 8;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            int n = remaining.Count;
            for (int i = 0; i < n; i++)
            {
                int previous = remaining[(i + n - 1) % n];
                int current = remaining[i];
                int next = remaining[(i + 1) % n];
                Vector2 a = flat[previous];
                Vector2 b = flat[current];
                Vector2 c = flat[next];

                if (Cross(b - a, c - b) <= 1e-12f)
                    continue;

                bool containsVertex = false;
                for (int j = 0; j < n; j++)
                {
                    int other = remaining[j];
                    if (other == previous || other == current || other == next)
                        continue;

                    if (InsideTriangle(flat[other], a, b, c))
                    {
                        containsVertex = true;
                        break;
                    }
                }

                if (containsVertex)
                    continue;

                triangles.Add(previous);
                triangles.Add(current);
                triangles.Add(next);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
                break;
        }

        // The last triangle, or a plain fan when a self-intersecting outline
        // left no ear: an approximate fill is better than none while drawing.
        for (int i = 1; i + 1 < remaining.Count; i++)
        {
            triangles.Add(remaining[0]);
            triangles.Add(remaining[i]);
            triangles.Add(remaining[i + 1]);
        }

        if (triangles.Count < 3)
            return false;

        colors.Clear();
        for (int i = 0; i < count; i++)
            colors.Add(color);

        mesh.Clear();
        mesh.SetVertices(ring);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        applied.Clear();
        applied.AddRange(ring);
        appliedColor = color;
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static bool InsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        return Cross(b - a, point - a) >= 0f && Cross(c - b, point - b) >= 0f && Cross(a - c, point - c) >= 0f;
    }

    private bool EnsureFill()
    {
        if (fillObject != null)
            return true;

        Material material = ResolveMaterial();
        if (material == null)
            return false;

        if (container == null)
            container = new GameObject("Polygon Fills").transform;

        fillObject = new GameObject("PolygonFill_" + gameObject.name);
        fillObject.layer = gameObject.layer;
        fillObject.transform.SetParent(container, false);

        mesh = new Mesh();
        mesh.name = fillObject.name;
        mesh.MarkDynamic();
        fillObject.AddComponent<MeshFilter>().sharedMesh = mesh;

        fillRenderer = fillObject.AddComponent<MeshRenderer>();
        fillRenderer.sharedMaterial = material;
        fillRenderer.shadowCastingMode = ShadowCastingMode.Off;
        fillRenderer.receiveShadows = false;
        fillRenderer.lightProbeUsage = LightProbeUsage.Off;
        fillRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        fillRenderer.sortingOrder = FillSortingOrder;

        fillObject.SetActive(false);
        return true;
    }

    private static Material ResolveMaterial()
    {
        if (fillMaterial != null)
            return fillMaterial;

        // Needs vertex colours, alpha blending and no depth write. An opaque
        // fallback would hide the map, so without this shader there is no fill.
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            if (!missingShaderLogged)
            {
                TriffidLog.Warning("PolygonFill", "Sprites/Default shader is unavailable; polygons keep their outline only.");
                missingShaderLogged = true;
            }
            return null;
        }

        fillMaterial = new Material(shader);
        fillMaterial.name = "Polygon Fill (runtime)";
        fillMaterial.hideFlags = HideFlags.HideAndDontSave;
        return fillMaterial;
    }

    private void SetVisible(bool visible)
    {
        if (fillObject != null && fillObject.activeSelf != visible)
            fillObject.SetActive(visible);
    }
}
