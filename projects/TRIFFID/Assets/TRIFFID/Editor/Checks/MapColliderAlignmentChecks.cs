using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Native Unity transform and physics checks; no project scene is modified.</summary>
public static class MapColliderAlignmentChecks
{
    private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
    private static readonly Vector3[] probes = {
        Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
        new Vector3(1.3f, -2.1f, 0.7f), new Vector3(-4f, 0.3f, 2f)
    };

    [MenuItem("Tools/Experiments/Check Renderer Collider Alignment")]
    public static void RunMenu()
    {
        if (Run() != 0) throw new InvalidOperationException("Renderer/collider alignment checks failed.");
    }

    public static int Run()
    {
        int failed = 0;
        Check("full signed hierarchy, original coordinates and later map manipulation", () => {
            Transform map = Node("alignment test map");
            map.localPosition = new Vector3(2f, -3f, 4f);
            map.localRotation = Quaternion.Euler(13f, 27f, -9f);
            map.localScale = new Vector3(0.3f, 0.5f, 0.7f);
            Transform renderer = Node("alignment test renderer", map);
            renderer.localPosition = new Vector3(0.4f, -0.2f, 0.6f);
            renderer.localRotation = Quaternion.Euler(-21f, 16f, 8f);
            renderer.localScale = new Vector3(-1f, 1f, 1f);
            Transform oldParent = Node("old parent");
            oldParent.localScale = new Vector3(2f, 3f, 4f);
            Transform target = Node("alignment test collider", oldParent);
            target.localPosition = new Vector3(11f, -4f, 7f);
            target.localRotation = Quaternion.Euler(123f, 29f, 17f);
            target.localScale = Vector3.one * 9f;
            Matrix4x4 sourceBefore = renderer.localToWorldMatrix;
            MapColliderSync sync = Follower(renderer, target);
            sync.SyncToMap();
            Require(target.parent == renderer, "Target must inherit the renderer hierarchy.");
            Equal(sourceBefore, renderer.localToWorldMatrix);
            Equal(renderer.localScale, new Vector3(-1f, 1f, 1f));
            Equal(target.localScale, Vector3.one);
            SameFrame(renderer, target, false);
            Require(target.localToWorldMatrix.determinant < 0f, "Negative handedness was lost.");
            for (int i = 0; i < 25; i++) sync.SyncToMap();
            SameFrame(renderer, target, false);
            // No further Sync is required for movement/rotation/scale inheritance.
            map.localPosition += new Vector3(5f, 2f, -3f);
            map.localRotation = Quaternion.Euler(-11f, 63f, 7f);
            map.localScale = new Vector3(0.04f, 0.08f, 0.12f);
            renderer.localRotation = Quaternion.Euler(31f, -7f, 23f);
            SameFrame(renderer, target, false);
            foreach (Vector3 p in probes)
                Equal(renderer.InverseTransformPoint(target.TransformPoint(p)), p);
        }, ref failed);

        Check("known imported X reflection is compensated exactly once", () => {
            Transform renderer = Node("renderer");
            renderer.localScale = new Vector3(-0.3f, 0.4f, 0.5f);
            renderer.localRotation = Quaternion.Euler(14f, 23f, 37f);
            Transform target = Node("mesh");
            MapColliderSync sync = Follower(renderer, target);
            sync.meshVertexAxes = MapColliderSync.MeshVertexAxes.AlreadyMirroredX;
            sync.SyncToMap();
            SameFrame(renderer, target, true);
            sync.meshVertexAxes = MapColliderSync.MeshVertexAxes.OriginalPly;
            sync.SyncToMap();
            SameFrame(renderer, target, false);
        }, ref failed);

        Check("invalid hierarchy and missing references make no changes", () => {
            Transform ancestor = Node("ancestor");
            Transform renderer = Node("renderer", ancestor);
            MapColliderSync sync = Follower(renderer, ancestor);
            Require(!sync.TryValidateSetup(out _), "Ancestor target must be rejected.");
            sync.SyncToMap();
            Require(renderer.parent == ancestor && ancestor.parent == null, "Invalid sync changed the hierarchy.");
            sync.MeshColliderObject = renderer;
            Require(!sync.TryValidateSetup(out _), "Self target must be rejected.");
            sync.MeshColliderObject = null;
            Require(!sync.TryValidateSetup(out _), "Missing target must be rejected.");
            sync.MeshColliderObject = Node("mesh");
            sync.GaussianRenderer = null;
            Require(!sync.TryValidateSetup(out _), "Missing source must be rejected.");
        }, ref failed);

        Check("disabled component ignores direct aligner calls", () => {
            Transform renderer = Node("renderer");
            Transform target = Node("mesh");
            target.localPosition = Vector3.one;
            MapColliderSync sync = Follower(renderer, target);
            sync.enabled = false;
            sync.SyncToMap();
            Require(target.parent == null, "Disabled sync reparented the target.");
            Equal(target.localPosition, Vector3.one);
        }, ref failed);

        Check("readable MeshCollider raycast follows reflected renderer", () => {
            Transform map = Node("map");
            map.localPosition = new Vector3(2f, 3f, -4f);
            map.localScale = Vector3.one * 0.3f;
            Transform renderer = Node("renderer", map);
            renderer.localScale = new Vector3(-1f, 1f, 1f);
            renderer.localRotation = Quaternion.Euler(12f, 23f, 4f);
            Mesh mesh = CubeMesh(); // readable transient geometry, original XYZ
            owned.Add(mesh);
            Transform target = Node("mesh collider");
            MeshCollider collider = target.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            Follower(renderer, target).SyncToMap();
            Physics.SyncTransforms();
            Vector3 origin = renderer.TransformPoint(new Vector3(0f, 0f, 3f));
            Vector3 expected = renderer.TransformPoint(Vector3.forward);
            Ray ray = new Ray(origin, (expected - origin).normalized);
            Require(collider.Raycast(ray, out RaycastHit hit, 100f), "Ray missed reflected collider.");
            Equal(hit.point, expected);
            Require(collider.sharedMesh == mesh, "Following replaced mesh geometry.");
        }, ref failed);

        Debug.Log("RENDERER COLLIDER CHECKS: " + (5 - failed) + " passed, " + failed + " failed.");
        return failed;
    }

    private static MapColliderSync Follower(Transform source, Transform target)
    {
        MapColliderSync sync = target.gameObject.AddComponent<MapColliderSync>();
        sync.GaussianRenderer = source;
        sync.MeshColliderObject = target;
        return sync;
    }

    private static Transform Node(string name, Transform parent = null)
    {
        GameObject obj = new GameObject(name);
        owned.Add(obj);
        obj.transform.SetParent(parent, false);
        return obj.transform;
    }

    private static void SameFrame(Transform renderer, Transform target, bool mirroredVertices)
    {
        foreach (Vector3 p in probes)
        {
            Vector3 vertex = mirroredVertices ? new Vector3(-p.x, p.y, p.z) : p;
            Equal(target.TransformPoint(vertex), renderer.TransformPoint(p));
        }
    }

    private static void Equal(Vector3 actual, Vector3 expected)
    {
        Require((actual - expected).magnitude < 0.0001f, "World/local point mismatch: " + actual + " vs " + expected);
    }

    private static void Equal(Matrix4x4 actual, Matrix4x4 expected)
    {
        foreach (Vector3 p in probes) Equal(actual.MultiplyPoint3x4(p), expected.MultiplyPoint3x4(p));
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Check(string name, Action body, ref int failed)
    {
        try { body(); Debug.Log("PASS " + name); }
        catch (Exception e) { failed++; Debug.LogError("FAIL " + name + ": " + e); }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--)
                if (owned[i] != null) UnityEngine.Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }
    }

    private static Mesh CubeMesh()
    {
        var mesh = new Mesh();
        mesh.vertices = new[] {
            new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
            new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1)
        };
        mesh.triangles = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
            3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
