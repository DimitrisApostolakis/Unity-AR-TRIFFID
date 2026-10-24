using System;
using UnityEditor;
using UnityEngine;

public static class AR2ExperimentChecks
{
    private static int physicsPassed, physicsFailed;

    [MenuItem("AR2/Experiments/Run Regression Checks")]
    public static void Run()
    {
        int core = AR2CoreRegressionTests.Run();
        physicsPassed = physicsFailed = 0;
        Vector3[] scales = { Vector3.one, Vector3.one * 0.01f, Vector3.one * 3f,
            new Vector3(-1f, 1f, 1f), new Vector3(-0.4f, 0.7f, 1.3f) };
        for (int configuration = 0; configuration < scales.Length; configuration++)
        {
            foreach (float height in new[] { -2f, 0f, 2f })
            {
                int index = configuration;
                float offset = height;
                Check("surface offset " + offset + " m, display configuration " + index,
                    () => CheckSurface(scales[index], index, offset, false));
            }
            int outsideIndex = configuration;
            Check("out-of-mesh query stays unresolved, display configuration " + configuration,
                () => CheckSurface(scales[outsideIndex], outsideIndex, 0f, true));
        }
        Check("invalid calibration scale is rejected", () =>
        {
            GameObject map = new GameObject("AR2 invalid-scale check");
            map.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                Vector3 up; float units;
                Require(!GeographicSurfaceQuery.TryGetWorldVertical(map.transform,
                    new[] { 1f,0f,0f,0f,1f,0f,0f,0f,1f }, 0f, out up, out units), "Zero scale was accepted.");
            }
            finally { UnityEngine.Object.DestroyImmediate(map); }
        });
        Check("unresolved elevation is visibly flagged and restores the marker colour", () =>
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.hideFlags = HideFlags.HideAndDontSave;
            Material material = null;
            try
            {
                Renderer renderer = marker.GetComponent<Renderer>();
                material = new Material(renderer.sharedMaterial);
                material.hideFlags = HideFlags.HideAndDontSave;
                material.color = Color.green;
                renderer.sharedMaterial = material;
                PointData data = marker.AddComponent<PointData>();
                data.SetElevationUnresolved(true);
                Material highlight = renderer.sharedMaterial;
                Require(data.elevationUnresolved && highlight != material && highlight.color != Color.green, "Unresolved marker has no visible flag.");
                Require(material.color == Color.green, "Highlight changed the original material.");
                if (!Application.isPlaying)
                {
                    data.enabled = false;
                    Require(highlight == null && renderer.sharedMaterial == material, "Disabled component leaked its temporary material.");
                    Require(data.elevationUnresolved, "Disabling the component lost the unresolved status.");
                    data.enabled = true;
                    highlight = renderer.sharedMaterial;
                    Require(highlight != material && highlight.color != Color.green, "Re-enabled component did not restore its highlight.");
                    marker.SetActive(false);
                    Require(highlight == null && renderer.sharedMaterial == material, "Inactive marker leaked its temporary material.");
                    marker.SetActive(true);
                    highlight = renderer.sharedMaterial;
                    Require(highlight != material && highlight.color != Color.green, "Reactivated marker did not restore its highlight.");
                }
                data.SetElevationUnresolved(false);
                Require(!data.elevationUnresolved && renderer.sharedMaterial == material && material.color == Color.green, "Resolved marker retained the unresolved colour.");
                if (!Application.isPlaying) Require(highlight == null, "Resolved marker leaked its temporary material.");
                data.SetElevationUnresolved(true);
                highlight = renderer.sharedMaterial;
                UnityEngine.Object.DestroyImmediate(marker);
                if (!Application.isPlaying) Require(highlight == null, "Destroyed marker leaked its temporary material.");
            }
            finally
            {
                if (marker != null) UnityEngine.Object.DestroyImmediate(marker);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        });
        string result = "AR2 regression checks: core " + (core == 0 ? "passed" : "FAILED") +
            ", Unity physics " + physicsPassed + " passed, " + physicsFailed + " failed.";
        if (core != 0 || physicsFailed != 0)
        {
            Debug.LogError(result);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
        else
        {
            Debug.Log(result);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }

    private static void CheckSurface(Vector3 scale, int configuration, float height, bool outside)
    {
        GameObject parent = new GameObject("AR2 temporary test hierarchy");
        parent.hideFlags = HideFlags.HideAndDontSave;
        GameObject map = new GameObject("AR2 temporary map");
        map.hideFlags = HideFlags.HideAndDontSave;
        map.transform.SetParent(parent.transform, false);
        GameObject surface = new GameObject("AR2 temporary reference surface");
        surface.hideFlags = HideFlags.HideAndDontSave;
        surface.transform.SetParent(map.transform, false);
        Mesh mesh = new Mesh();
        mesh.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            parent.transform.SetPositionAndRotation(new Vector3(3f, 4f, -2f), Quaternion.Euler(11f, -19f, 7f));
            if (configuration == 4) parent.transform.localScale = new Vector3(0.8f, 1.2f, 0.9f);
            map.transform.localPosition = new Vector3(2f, -1f, 3f);
            map.transform.localRotation = Quaternion.Euler(23f, 41f, -13f);
            map.transform.localScale = scale;
            Matrix4x4 rotation = Matrix4x4.Rotate(Quaternion.Euler(17f, 25f, -12f));
            float[] r = { rotation.m00, rotation.m01, rotation.m02, rotation.m10, rotation.m11, rotation.m12,
                rotation.m20, rotation.m21, rotation.m22 };
            const float metricScale = 2.5f;
            Vector3 east = new Vector3(r[0], r[1], r[2]) / metricScale;
            Vector3 north = new Vector3(r[3], r[4], r[5]) / metricScale;
            Vector3 localUp = new Vector3(r[6], r[7], r[8]) / metricScale;
            mesh.vertices = new[] { -20f * east - 20f * north, 20f * east - 20f * north,
                20f * east + 20f * north, -20f * east + 20f * north };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            MeshCollider collider = surface.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            Physics.SyncTransforms();

            Vector3 worldUp; float units;
            Require(GeographicSurfaceQuery.TryGetWorldVertical(map.transform, r, metricScale, out worldUp, out units), "No vertical axis.");
            // Independent oracle: transform two positions rather than a vector.
            Vector3 twoPointAxis = map.transform.TransformPoint(localUp) - map.transform.TransformPoint(Vector3.zero);
            Require((twoPointAxis - worldUp * units).magnitude < 1e-4f, "Vertical direction differs from the two-point transform.");
            Vector3 markerLocal = localUp * height + (outside ? east * 1000f : Vector3.zero);
            Vector3 marker = map.transform.TransformPoint(markerLocal);
            Vector3 hit;
            bool found = GeographicSurfaceQuery.TryHit(collider, marker, worldUp, 25f * units, 100f * units, ~0, out hit);
            if (outside) { Require(!found, "An out-of-mesh point was relocated onto the mesh."); return; }
            Require(found, "No reference surface intersection.");
            Vector3 localDelta = map.transform.InverseTransformPoint(marker) - map.transform.InverseTransformPoint(hit);
            Vector3 enu = rotation.MultiplyVector(localDelta) * metricScale;
            Require(Mathf.Abs(enu.z - height) < 0.005f, "Signed height changed under map manipulation: " + enu.z);
            Require(new Vector2(enu.x, enu.y).magnitude < 0.005f, "Surface search changed horizontal position.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(parent);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static void Check(string name, Action action)
    {
        try { action(); physicsPassed++; Debug.Log("PASS " + name); }
        catch (Exception ex) { physicsFailed++; Debug.LogError("FAIL " + name + ": " + ex); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
