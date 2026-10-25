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
                Check("disabled-collider surface offset " + offset + " m, display configuration " + index,
                    () => CheckSurface(scales[index], index, offset, false, true));
            }
            int outsideIndex = configuration;
            Check("out-of-mesh query stays unresolved, display configuration " + configuration,
                () => CheckSurface(scales[outsideIndex], outsideIndex, 0f, true));
            Check("disabled-collider miss restores state, display configuration " + configuration,
                () => CheckSurface(scales[outsideIndex], outsideIndex, 0f, true, true));
        }
        Check("panel selection survives background updates and marker rebuilding", CheckPanelSelection);
        Check("pointer placement queries a disabled mesh collider", CheckPointerPlacement);
        Check("GeoJSON rebuilding preserves telemetry and drafts and releases saved stroke nodes", CheckRuntimeOwnership);
        Check("switching drawing tools finalizes the previous geometry and cancel removes draft vertices", CheckDrawingTools);
        Check("POI filters survive marker rebuilding and apply to incoming markers", CheckFilterPersistence);
        Check("surface reactivation reuses its mesh and keeps raycasts available", CheckMeshCache);
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

    private static void CheckMeshCache()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ar2_unity_mesh_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        GameObject surface = new GameObject("AR2 temporary cached surface");
        surface.hideFlags = HideFlags.HideAndDontSave;
        MeshPathJsonLoader loader = null;
        try
        {
            string path = System.IO.Path.Combine(directory, "surface.obj");
            System.IO.File.WriteAllText(path, "v 0 0 0\nv 0 0 1\nv 1 0 0\nf 1 2 3\n");
            loader = surface.AddComponent<MeshPathJsonLoader>();
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(MeshPathJsonLoader).GetField("meshPath", flags).SetValue(loader, path);
            typeof(MeshPathJsonLoader).GetField("meshLoadMode", flags).SetValue(loader, MeshLoadMode.ExternalObjFile);
            typeof(MeshPathJsonLoader).GetField("loadOnEnable", flags).SetValue(loader, true);
            typeof(MeshPathJsonLoader).GetField("loadOnStart", flags).SetValue(loader, true);
            typeof(MeshPathJsonLoader).GetField("ensureMeshRenderer", flags).SetValue(loader, false);
            typeof(MeshPathJsonLoader).GetField("logDetails", flags).SetValue(loader, false);
            // Invoke the callback explicitly because this menu check runs in Edit mode.
            System.Reflection.MethodInfo enable = typeof(MeshPathJsonLoader).GetMethod("OnEnable", flags);
            enable.Invoke(loader, null);
            Mesh original = surface.GetComponent<MeshFilter>().sharedMesh;
            for (int i = 0; i < 10; i++) enable.Invoke(loader, null);
            typeof(MeshPathJsonLoader).GetMethod("Start", flags).Invoke(loader, null);
            Require(loader.MeshLoadCount == 1 && loader.ColliderAssignmentCount == 1,
                "An unchanged surface was reloaded or its collider was reassigned.");
            Require(surface.GetComponent<MeshFilter>().sharedMesh == original, "Cached mesh identity changed.");
            Physics.SyncTransforms();
            Vector3 hit;
            Require(GeographicSurfaceQuery.TryHit(surface.GetComponent<MeshCollider>(),
                new Vector3(0.2f, 0.5f, 0.2f), Vector3.up, 1f, 3f, ~0, out hit) && Mathf.Abs(hit.y) < 0.001f,
                "Cached collider is no longer available for surface queries.");
            loader.LoadAndApplyMesh();
            Require(loader.MeshLoadCount == 2 && loader.ColliderAssignmentCount == 2, "Explicit reload did not refresh the source.");
            if (!Application.isPlaying) Require(original == null, "Explicit reload retained the replaced runtime mesh.");
        }
        finally
        {
            if (loader != null)
                typeof(MeshPathJsonLoader).GetMethod("ReleaseLoadedMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(loader, null);
            UnityEngine.Object.DestroyImmediate(surface);
            System.IO.Directory.Delete(directory, true);
        }
    }

    private static void CheckSurface(Vector3 scale, int configuration, float height, bool outside, bool disabled = false)
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
            collider.enabled = !disabled;
            bool found = GeographicSurfaceQuery.TryHit(collider, marker, worldUp, 25f * units, 100f * units, ~0, out hit);
            Require(collider.enabled == !disabled, "Surface query changed the interaction collider state.");
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

    private static void CheckPanelSelection()
    {
        GameObject root = new GameObject("AR2 temporary panel selection check");
        root.hideFlags = HideFlags.HideAndDontSave;
        PointInfoPanel panel = root.AddComponent<PointInfoPanel>();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var disable = typeof(PointInfoPanel).GetMethod("OnDisable", flags);
        try
        {
            // Invoke lifecycle hooks explicitly for this Edit-mode menu check.
            disable.Invoke(panel, null);
            typeof(PointInfoPanel).GetMethod("OnEnable", flags).Invoke(panel, null);
            PointData a = CreatePanelMarker(root, "a"), b = CreatePanelMarker(root, "b");
            panel.RegisterMarker(a, null); panel.RegisterMarker(b, null); panel.OnMarkerSelected(b);
            MarkerEventManager.RaiseMarkerHovered(a);
            Require(panel.IsObservingMarker(a), "Ray hover did not select the hovered point.");
            panel.LiveUpdateIfCurrent(b);
            Require(panel.IsObservingMarker(a), "Background update changed the hovered point.");
            panel.OnMarkerSelected(b);
            panel.BeginMarkerRefresh(); panel.ClearMarkers();
            PointData newB = CreatePanelMarker(root, "b"), newA = CreatePanelMarker(root, "a");
            panel.RegisterMarker(newB, null); panel.RegisterMarker(newA, null); panel.EndMarkerRefresh();
            Require(panel.IsObservingMarker(newB), "Rebuilding markers lost the selected feature.");
            panel.BeginMarkerRefresh(); panel.ClearMarkers(); panel.RegisterMarker(newA, null); panel.EndMarkerRefresh();
            Require(!panel.IsObservingMarker(newA), "Deleting the selected point switched to an unrelated point.");
        }
        finally
        {
            disable.Invoke(panel, null);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static PointData CreatePanelMarker(GameObject parent, string id)
    {
        GameObject marker = new GameObject("AR2 temporary panel marker " + id);
        marker.hideFlags = HideFlags.HideAndDontSave;
        marker.transform.SetParent(parent.transform, false);
        PointData data = marker.AddComponent<PointData>();
        data.pointID = id;
        data.pointClass = "animal";
        return data;
    }

    private static void CheckPointerPlacement()
    {
        GameObject surface = new GameObject("AR2 temporary placement surface");
        surface.hideFlags = HideFlags.HideAndDontSave;
        Mesh mesh = new Mesh();
        mesh.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            mesh.vertices = new[] { new Vector3(-1f,0f,-1f),new Vector3(-1f,0f,1f),new Vector3(1f,0f,1f),new Vector3(1f,0f,-1f) };
            mesh.triangles = new[] { 0,1,2,0,2,3 };
            mesh.RecalculateBounds();
            MeshCollider collider = surface.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.enabled = false;
            RaycastHit hit;
            Require(GeographicSurfaceQuery.TryRaycast(collider, new Ray(Vector3.up * 2f, Vector3.down), 5f, ~0, out hit),
                "Pointer placement missed a disabled reference mesh.");
            Require(!collider.enabled && collider.sharedMesh == mesh && Mathf.Abs(hit.point.y) < 0.001f,
                "Placement changed the collider state or mesh.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(surface);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static void CheckRuntimeOwnership()
    {
        GameObject root = new GameObject("AR2 temporary annotation ownership check");
        root.hideFlags = HideFlags.HideAndDontSave;
        root.SetActive(false);
        try
        {
            JsonSpawner spawner = root.AddComponent<JsonSpawner>();
            spawner.mapTransform = root.transform;
            GameObject ugv = CreatePanelMarker(root, "latest_mqtt_icon").gameObject;
            GameObject geo = CreatePanelMarker(root, "feature_a").gameObject;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var spawned = (System.Collections.Generic.List<GameObject>)typeof(JsonSpawner).GetField("spawnedObjects", flags).GetValue(spawner);
            spawned.Add(ugv); spawned.Add(geo);
            GameObject strokeObject = new GameObject("AR2 temporary draft stroke");
            strokeObject.transform.SetParent(root.transform, false);
            RuntimeLine stroke = strokeObject.AddComponent<RuntimeLine>();
            stroke.Initialize(strokeObject.AddComponent<LineRenderer>(), true);
            GameObject node = new GameObject("AR2 temporary draft vertex");
            node.transform.SetParent(root.transform, false);
            stroke.AddNode(node.transform);
            spawner.RegisterRuntimeStroke(stroke);
            var clear = typeof(JsonSpawner).GetMethod("ClearSpawnedObjects", flags, null, new[] { typeof(bool) }, null);
            clear.Invoke(spawner, new object[] { true });
            Require(spawned.Contains(ugv) && !spawned.Contains(geo) && stroke != null && node != null && spawner.HasActiveAnnotationDraft,
                "Refreshing GeoJSON removed telemetry/draft objects or retained a stale feature.");
            stroke.BindToFeature("saved_annotation");
            clear.Invoke(spawner, new object[] { true });
            Require(spawned.Contains(ugv) && !spawner.HasActiveAnnotationDraft, "Saved stroke cleanup disturbed telemetry or retained a draft.");
            if (!Application.isPlaying) Require(stroke == null && node == null, "Saved stroke left orphan vertices.");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private sealed class DrawingCheckPersistence : IAnnotationPersistenceService, IAnnotationDraftRegistry
    {
        public int lines, polygons, strokes;
        public string SavePoint(GameObject point, string type) { return "point"; }
        public string SaveLine(System.Collections.Generic.IReadOnlyList<Vector3> points, string type, Color color) { lines++; return "line_" + lines; }
        public string SavePolygon(System.Collections.Generic.IReadOnlyList<Vector3> points, string type, string category, Color color) { polygons++; return "polygon_" + polygons; }
        public bool DeleteFeatureByNode(Transform node, bool requireDrawMode = false) { return true; }
        public void RegisterRuntimeLineNodes(string id, System.Collections.Generic.List<Transform> nodes) { }
        public void RegisterRuntimePolygonNodes(string id, System.Collections.Generic.List<Transform> nodes) { }
        public void RegisterRuntimeStroke(RuntimeLine stroke) { strokes++; }
    }

    private static void CheckDrawingTools()
    {
        GameObject root = new GameObject("AR2 temporary drawing tool check");
        root.hideFlags = HideFlags.HideAndDontSave;
        root.SetActive(false);
        Material material = new Material(Shader.Find("Hidden/InternalErrorShader"));
        try
        {
            XRControllerLogger logger = root.AddComponent<XRControllerLogger>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(XRControllerLogger).GetField("lineMaterial", flags).SetValue(logger, material);
            GameObject prefab = new GameObject("AR2 temporary drawing node template");
            prefab.transform.SetParent(root.transform, false);
            typeof(XRControllerLogger).GetField("lineNodePrefab", flags).SetValue(logger, prefab);
            var persistence = new DrawingCheckPersistence();
            logger.InjectDependencies(null, persistence);
            var session = typeof(XRControllerLogger).GetMethod("GetOrCreateCurrentLine", flags);
            var add = typeof(XRControllerLogger).GetMethod("CreateLineNode", flags);
            logger.SelectLineDrawingMode();
            object line = session.Invoke(logger, null);
            add.Invoke(logger, new object[] { line, Vector3.zero });
            add.Invoke(logger, new object[] { line, Vector3.right });
            logger.SelectPolygonDrawingMode();
            Require(persistence.lines == 1 && persistence.polygons == 0, "Changing to polygon mode did not finish the line first.");
            object polygon = session.Invoke(logger, null);
            add.Invoke(logger, new object[] { polygon, Vector3.zero });
            add.Invoke(logger, new object[] { polygon, Vector3.right });
            add.Invoke(logger, new object[] { polygon, Vector3.forward });
            logger.SelectLineDrawingMode();
            Require(persistence.polygons == 1, "Changing to line mode did not finish the polygon first.");
            object draft = session.Invoke(logger, null);
            Transform node = (Transform)add.Invoke(logger, new object[] { draft, Vector3.zero });
            logger.CancelCurrentAnnotation();
            if (!Application.isPlaying) Require(node == null, "Cancelling a draft retained its vertex.");
            Require(persistence.lines == 1, "Cancelling uploaded the unfinished draft.");
            Require(persistence.strokes == 3, "The persistence owner did not receive the three drawing sessions.");
        }
        finally
        {
            foreach (RuntimeLine line in root.GetComponentsInChildren<RuntimeLine>(true)) line.ReleaseOwnedObjects();
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(material);
        }
    }

    private static void CheckFilterPersistence()
    {
        GameObject root = new GameObject("AR2 temporary POI filter check");
        root.hideFlags = HideFlags.HideAndDontSave;
        root.SetActive(false);
        try
        {
            JsonSpawner spawner = root.AddComponent<JsonSpawner>();
            spawner.mapTransform = root.transform;
            spawner.FilterAnimals(false);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Type mappingType = typeof(JsonSpawner).GetNestedType("NodeMapping", System.Reflection.BindingFlags.NonPublic);
            var mappings = (System.Collections.IList)typeof(JsonSpawner).GetField("nodeMappings", flags).GetValue(spawner);
            var apply = typeof(JsonSpawner).GetMethod("ApplyCurrentCategoryFilters", flags);
            var clear = typeof(JsonSpawner).GetMethod("ClearSpawnedObjects", flags, null, new[] { typeof(bool) }, null);
            for (int rebuild = 0; rebuild < 2; rebuild++)
            {
                GameObject animal = CreatePanelMarker(root, "animal_a").gameObject;
                GameObject citizen = CreatePanelMarker(root, "citizen_b").gameObject;
                foreach (var item in new[] { animal, citizen })
                {
                    string type = item == animal ? "animal" : "citizen";
                    object mapping = Activator.CreateInstance(mappingType, true);
                    mappingType.GetField("node").SetValue(mapping, item.transform);
                    mappingType.GetField("parentFeature").SetValue(mapping, new Feature {
                        id = item.GetComponent<PointData>().pointID, properties = new Properties { className = type },
                        geometry = new Geometry { type = "Point" } });
                    mappings.Add(mapping);
                }
                apply.Invoke(spawner, null);
                Require(!animal.activeSelf && citizen.activeSelf, "Recreated POIs ignored the cached filter.");
                if (rebuild == 0) clear.Invoke(spawner, new object[] { true });
                else
                {
                    spawner.FilterAnimals(true);
                    Require(animal.activeSelf && citizen.activeSelf, "Turning the filter back on did not restore the POI.");
                }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void Check(string name, Action action)
    {
        try { action(); physicsPassed++; Debug.Log("PASS " + name); }
        catch (Exception ex) { physicsFailed++; Debug.LogError("FAIL " + name + ": " + ex); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
