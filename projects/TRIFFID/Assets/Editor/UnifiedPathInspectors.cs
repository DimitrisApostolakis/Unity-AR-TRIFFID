using UnityEditor;
using UnityEngine;

internal static class UnifiedPathInspectorGui
{
    public static void DrawSinglePathLoading(
        SerializedProperty customPath,
        SerializedProperty inputRoot,
        SerializedProperty useSharedJson,
        SerializedProperty sharedJsonFile,
        SerializedProperty jsonKey,
        string customPathLabel,
        string rootLabel = "Input Path Root")
    {
        EditorGUILayout.PropertyField(useSharedJson, new GUIContent("Use Shared Path JSON"));
        EditorGUI.indentLevel++;
        if (useSharedJson.boolValue)
        {
            EditorGUILayout.PropertyField(sharedJsonFile, new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(jsonKey, new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent(rootLabel));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its path value uses the selected path root.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(customPath, new GUIContent(customPathLabel));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent(rootLabel));
        }
        EditorGUI.indentLevel--;
    }

    public static void DrawActionButtons(params (string Label, System.Action Action)[] actions)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            for (int i = 0; i < actions.Length; i++)
            {
                if (GUILayout.Button(actions[i].Label))
                    actions[i].Action();
            }
        }
    }
}

[CustomEditor(typeof(MeshPathJsonLoader))]
[CanEditMultipleObjects]
public sealed class MeshPathJsonLoaderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Mesh Path Loading", EditorStyles.boldLabel);
        SerializedProperty pathSource = serializedObject.FindProperty("pathSource");
        bool useShared = pathSource.enumValueIndex == (int)PathSource.JsonFile;
        EditorGUI.showMixedValue = pathSource.hasMultipleDifferentValues;
        bool updatedUseShared = EditorGUILayout.Toggle(new GUIContent("Use Shared Path JSON"), useShared);
        EditorGUI.showMixedValue = false;
        if (updatedUseShared != useShared)
            pathSource.enumValueIndex = updatedUseShared ? (int)PathSource.JsonFile : (int)PathSource.ManualOverride;

        EditorGUI.indentLevel++;
        if (updatedUseShared)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("jsonConfigPath"), new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("meshPathJsonKey"), new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its mesh path uses Input Path Root.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("manualMeshPath"), new GUIContent("Custom Mesh Path"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
        }
        EditorGUI.indentLevel--;

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "pathSource",
            "inputPathRoot",
            "jsonConfigPath",
            "meshPathJsonKey",
            "manualMeshPath");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var loader = (MeshPathJsonLoader)target;
            UnifiedPathInspectorGui.DrawActionButtons(
                ("Validate Path", loader.ValidatePaths),
                ("Load Mesh", loader.LoadAndApplyMesh));
        }
    }
}

[CustomEditor(typeof(ColmapGaussianRotationAligner))]
[CanEditMultipleObjects]
public sealed class ColmapGaussianRotationAlignerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.LabelField("Transform Path Loading", EditorStyles.boldLabel);
        UnifiedPathInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("transformJsonPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedTransformPathJson"),
            serializedObject.FindProperty("sharedTransformPathsJsonFile"),
            serializedObject.FindProperty("transformPathJsonKey"),
            "Custom Transform Path");

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "transformJsonPath",
            "inputPathRoot",
            "useSharedTransformPathJson",
            "sharedTransformPathsJsonFile",
            "transformPathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var aligner = (ColmapGaussianRotationAligner)target;
            UnifiedPathInspectorGui.DrawActionButtons(
                ("Validate Path", aligner.ValidateTransformJson),
                ("Apply Alignment", aligner.ApplyJsonRotationAndSyncCollider));
        }
    }
}

[CustomEditor(typeof(JsonSpawner))]
[CanEditMultipleObjects]
public sealed class JsonSpawnerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("GeoJSON Input Path Loading", EditorStyles.boldLabel);
        UnifiedPathInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("geoJsonPath"),
            serializedObject.FindProperty("geoJsonInputPathRoot"),
            serializedObject.FindProperty("useSharedGeoJsonPathJson"),
            serializedObject.FindProperty("sharedGeoJsonPathsJsonFile"),
            serializedObject.FindProperty("geoJsonPathJsonKey"),
            "Custom GeoJSON Input Path");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform Path Loading", EditorStyles.boldLabel);
        UnifiedPathInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("transformJsonPath"),
            serializedObject.FindProperty("transformInputPathRoot"),
            serializedObject.FindProperty("useSharedTransformPathJson"),
            serializedObject.FindProperty("sharedTransformPathsJsonFile"),
            serializedObject.FindProperty("transformPathJsonKey"),
            "Custom Transform Path");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("GeoJSON Output Path Loading", EditorStyles.boldLabel);
        UnifiedPathInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("saveFilePath"),
            serializedObject.FindProperty("saveOutputPathRoot"),
            serializedObject.FindProperty("useSharedSavePathJson"),
            serializedObject.FindProperty("sharedSavePathsJsonFile"),
            serializedObject.FindProperty("savePathJsonKey"),
            "Custom GeoJSON Output Path",
            "Output Path Root");

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "geoJsonPath",
            "geoJsonInputPathRoot",
            "useSharedGeoJsonPathJson",
            "sharedGeoJsonPathsJsonFile",
            "geoJsonPathJsonKey",
            "transformJsonPath",
            "transformInputPathRoot",
            "useSharedTransformPathJson",
            "sharedTransformPathsJsonFile",
            "transformPathJsonKey",
            "saveFilePath",
            "saveOutputPathRoot",
            "useSharedSavePathJson",
            "sharedSavePathsJsonFile",
            "savePathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var spawner = (JsonSpawner)target;
            UnifiedPathInspectorGui.DrawActionButtons(
                ("Validate Paths", spawner.ValidateConfiguredPaths),
                ("Spawn From JSON", spawner.SpawnFromJson),
                ("Save To JSON", spawner.SaveToJson));
        }
    }
}
