using UnityEditor;
using UnityEngine;

internal static class ArPipelineInspectorGui
{
    public static void DrawSinglePathLoading(
        SerializedProperty customPath,
        SerializedProperty inputRoot,
        SerializedProperty useSharedJson,
        SerializedProperty sharedJsonFile,
        SerializedProperty jsonKey,
        string customPathLabel)
    {
        EditorGUILayout.PropertyField(useSharedJson, new GUIContent("Use Shared Path JSON"));
        EditorGUI.indentLevel++;
        if (useSharedJson.boolValue)
        {
            EditorGUILayout.PropertyField(sharedJsonFile, new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(jsonKey, new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its path value uses Input Path Root.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(customPath, new GUIContent(customPathLabel));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
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
        ArPipelineInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("meshPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedMeshPathJson"),
            serializedObject.FindProperty("sharedMeshPathsJsonFile"),
            serializedObject.FindProperty("meshPathJsonKey"),
            "Custom Mesh Path");

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "meshPath",
            "inputPathRoot",
            "useSharedMeshPathJson",
            "sharedMeshPathsJsonFile",
            "meshPathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var loader = (MeshPathJsonLoader)target;
            ArPipelineInspectorGui.DrawActionButtons(
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
        ArPipelineInspectorGui.DrawSinglePathLoading(
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
            ArPipelineInspectorGui.DrawActionButtons(
                ("Validate Path", aligner.ValidateTransformJson),
                ("Apply Alignment", aligner.ApplyJsonRotationAndSyncCollider));
        }
    }
}
