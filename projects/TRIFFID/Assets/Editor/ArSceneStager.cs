using System;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Stages the AR scene for a job converted by <see cref="ArSplatInboxWatcher"/>: every scene
/// component is switched to the shared project_paths.json the watcher just wrote, then the same
/// actions as the Inspector buttons are run so the scene shows the new job in Edit Mode and
/// reloads it on Play.
/// </summary>
internal static class ArSceneStager
{
    private const string ScenePath = "Assets/Scenes/Harokopio.unity";
    private const string LogPrefix = "[ArSceneStager]";

    // Scene-side paths for one job, in the form they were written to project_paths.json.
    // A null path means the job did not carry that input.
    internal sealed class SceneInputs
    {
        public JObject Job;
        public string GaussianAssetPath;
        public string MeshPath;
        public string TransformPath;
    }

    public static void Stage(SceneInputs inputs)
    {
        try
        {
            StageScene(inputs);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} Staging '{ScenePath}' failed: {ex}");
        }
    }

    private static void StageScene(SceneInputs inputs)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
        {
            Debug.LogWarning($"{LogPrefix} Scene '{ScenePath}' does not exist; nothing staged.");
            return;
        }

        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedHere = !scene.isLoaded;
        if (openedHere)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

        bool hadUnsavedEdits = scene.isDirty;

        // Order matters: the aligner is pointed at the new geolocal JSON and the mesh is in place
        // before the rotation is applied and the collider is synced onto the mesh.
        StageGaussianRenderers(scene, inputs);
        StageRotationAligners(scene, inputs);
        StageJsonSpawners(scene, inputs);
        StageMeshLoaders(scene, inputs);
        ApplyAlignment(scene, inputs);
        StageDetections(scene, inputs);

        EditorSceneManager.MarkSceneDirty(scene);
        if (hadUnsavedEdits)
            Debug.LogWarning($"{LogPrefix} '{ScenePath}' had unsaved edits, so the staged job was applied but not saved.");
        else if (!EditorSceneManager.SaveScene(scene))
            Debug.LogWarning($"{LogPrefix} Could not save '{ScenePath}'.");

        if (openedHere)
            EditorSceneManager.CloseScene(scene, removeScene: true);

        Debug.Log($"{LogPrefix} Staged '{ScenePath}' for job '{inputs.Job?["job_id"]}'.");
    }

    private static void StageGaussianRenderers(Scene scene, SceneInputs inputs)
    {
        if (inputs.GaussianAssetPath == null)
        {
            Debug.LogWarning($"{LogPrefix} No Gaussian asset for this job; Gaussian renderers left unchanged.");
            return;
        }

        foreach (var renderer in FindInScene<GaussianSplatRenderer>(scene))
        {
            var so = new SerializedObject(renderer);
            SetBool(so, "m_LoadAssetFromPath", true);
            SetString(so, "m_AssetPath", inputs.GaussianAssetPath);
            SetEnum(so, "m_InputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "m_UseSharedAssetPathJson", true);
            SetString(so, "m_SharedAssetPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "m_AssetPathJsonKey", ArSplatInboxWatcher.GaussianAssetPathJsonKey);
            so.ApplyModifiedProperties();

            Undo.RecordObject(renderer, "Stage Gaussian asset");
            renderer.LoadAssetFromConfiguredPath();
        }
    }

    private static void StageRotationAligners(Scene scene, SceneInputs inputs)
    {
        if (inputs.TransformPath == null)
        {
            Debug.LogWarning($"{LogPrefix} No geolocal JSON for this job; rotation aligners keep their configuration.");
            return;
        }

        foreach (var aligner in FindInScene<ColmapGaussianRotationAligner>(scene))
        {
            var so = new SerializedObject(aligner);
            SetString(so, "transformJsonPath", inputs.TransformPath);
            SetEnum(so, "inputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "useSharedTransformPathJson", true);
            SetString(so, "sharedTransformPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "transformPathJsonKey", ArSplatInboxWatcher.ColmapTransformPathJsonKey);
            so.ApplyModifiedProperties();
        }
    }

    // The JSON spawner converts GeoJSON annotations to scene positions with the same geolocal
    // JSON, so it reads the aligner's key. It only spawns on Play; here the path is validated.
    private static void StageJsonSpawners(Scene scene, SceneInputs inputs)
    {
        if (inputs.TransformPath == null)
        {
            Debug.LogWarning($"{LogPrefix} No geolocal JSON for this job; JSON spawners keep their configuration.");
            return;
        }

        foreach (var spawner in FindInScene<JsonSpawner>(scene))
        {
            var so = new SerializedObject(spawner);
            SetString(so, "transformJsonPath", inputs.TransformPath);
            SetBool(so, "useSharedTransformPathJson", true);
            SetString(so, "sharedTransformPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "transformPathJsonKey", ArSplatInboxWatcher.ColmapTransformPathJsonKey);
            so.ApplyModifiedProperties();

            spawner.ValidateSharedTransformPath();
        }
    }

    private static void StageMeshLoaders(Scene scene, SceneInputs inputs)
    {
        if (inputs.MeshPath == null)
        {
            Debug.LogWarning($"{LogPrefix} No mesh OBJ for this job; mesh loaders left unchanged.");
            return;
        }

        foreach (var loader in FindInScene<MeshPathJsonLoader>(scene))
        {
            var so = new SerializedObject(loader);
            SetString(so, "meshPath", inputs.MeshPath);
            SetEnum(so, "inputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "useSharedMeshPathJson", true);
            SetString(so, "sharedMeshPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "meshPathJsonKey", ArSplatInboxWatcher.MeshPathJsonKey);
            so.ApplyModifiedProperties();

            GameObject target = so.FindProperty("targetObject")?.objectReferenceValue as GameObject ?? loader.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(target, "Stage mesh");
            loader.LoadAndApplyMesh();

            // An OBJ parsed from disk is a scene-local Mesh; keep it out of the saved scene so the
            // file does not grow by the whole mesh. The loader reloads it on Play.
            var filter = target.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : target.GetComponent<MeshCollider>()?.sharedMesh;
            if (mesh != null && !EditorUtility.IsPersistent(mesh))
                mesh.hideFlags |= HideFlags.DontSaveInEditor;

            if (!so.FindProperty("loadOnStart").boolValue && !so.FindProperty("loadOnEnable").boolValue)
                Debug.LogWarning($"{LogPrefix} '{loader.name}' has neither Load On Start nor Load On Enable set; the mesh will not load in Play Mode.", loader);
        }
    }

    // Runs after the mesh is staged so the collider lands on the new mesh. Without a geolocal JSON
    // there is no rotation to apply, but the collider is still synced to the splat.
    private static void ApplyAlignment(Scene scene, SceneInputs inputs)
    {
        foreach (var aligner in FindInScene<ColmapGaussianRotationAligner>(scene))
        {
            var moved = new List<UnityEngine.Object>();
            if (aligner.gaussianRenderer != null)
                moved.Add(aligner.gaussianRenderer);
            if (aligner.mapColliderSync != null && aligner.mapColliderSync.MeshColliderObject != null)
                moved.Add(aligner.mapColliderSync.MeshColliderObject);
            if (moved.Count > 0)
                Undo.RecordObjects(moved.ToArray(), "Stage geolocal alignment");

            if (inputs.TransformPath != null)
                aligner.ApplyJsonRotationAndSyncCollider();
            else
                aligner.SyncColliderNow();
        }
    }

    // Detections (3D detections JSON from the PC Unity instance) are not produced by the pipeline
    // yet. This is where they get wired once the AR job carries them.
    private static void StageDetections(Scene scene, SceneInputs inputs)
    {
    }

    private static List<T> FindInScene<T>(Scene scene) where T : Component
    {
        var found = new List<T>();
        foreach (var root in scene.GetRootGameObjects())
            found.AddRange(root.GetComponentsInChildren<T>(includeInactive: true));
        return found;
    }

    private static SerializedProperty FindProperty(SerializedObject so, string name)
    {
        var property = so.FindProperty(name);
        if (property == null)
            Debug.LogWarning($"{LogPrefix} '{so.targetObject.GetType().Name}' has no serialized field '{name}'; it was not staged.", so.targetObject);
        return property;
    }

    private static void SetString(SerializedObject so, string name, string value)
    {
        var property = FindProperty(so, name);
        if (property != null)
            property.stringValue = value;
    }

    private static void SetBool(SerializedObject so, string name, bool value)
    {
        var property = FindProperty(so, name);
        if (property != null)
            property.boolValue = value;
    }

    private static void SetEnum(SerializedObject so, string name, int value)
    {
        var property = FindProperty(so, name);
        if (property != null)
            property.enumValueIndex = value;
    }
}
