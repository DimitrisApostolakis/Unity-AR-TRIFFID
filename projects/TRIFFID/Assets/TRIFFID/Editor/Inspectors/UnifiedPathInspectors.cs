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
        if (useSharedJson.boolValue || useSharedJson.hasMultipleDifferentValues)
        {
            EditorGUILayout.PropertyField(sharedJsonFile, new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(jsonKey, new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its path value uses Input Path Root.",
                MessageType.None);
        }
        if (!useSharedJson.boolValue || useSharedJson.hasMultipleDifferentValues)
        {
            EditorGUILayout.PropertyField(customPath, new GUIContent(customPathLabel));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
        }
        EditorGUI.indentLevel--;
    }

    public static void DrawActionButtons(
        string firstLabel, System.Action firstAction,
        string secondLabel = null, System.Action secondAction = null)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(firstLabel)) firstAction();
            if (secondAction != null && GUILayout.Button(secondLabel)) secondAction();
        }
    }

}

[CustomEditor(typeof(MeshPathJsonLoader))]
[CanEditMultipleObjects]
public sealed class MeshPathJsonLoaderEditor : TriffidGroupedEditor
{
    protected override string Description => "Loads and caches the collision mesh. Alignment is configured separately on MapColliderSync and ColmapGaussianRotationAligner.";
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawInspectorToolbar();
        if (DrawSectionHeading("Path source", true))
        {
        ArPipelineInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("meshPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedMeshPathJson"),
            serializedObject.FindProperty("sharedMeshPathsJsonFile"),
            serializedObject.FindProperty("meshPathJsonKey"),
            "Custom Mesh Path");

        }
        DrawSections(
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
                "Validate Path", loader.ValidatePaths,
                "Load Mesh", loader.LoadAndApplyMesh);
        }
    }
}

[CustomEditor(typeof(ColmapGaussianRotationAligner))]
[CanEditMultipleObjects]
public sealed class ColmapGaussianRotationAlignerEditor : TriffidGroupedEditor
{
    protected override string Description => "Applies the COLMAP rotation to the splat renderer and can then call MapColliderSync. Position and scale are preserved.";
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawInspectorToolbar();
        if (DrawSectionHeading("Path source", true))
        {
        ArPipelineInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("transformJsonPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedTransformPathJson"),
            serializedObject.FindProperty("sharedTransformPathsJsonFile"),
            serializedObject.FindProperty("transformPathJsonKey"),
            "Custom Transform Path");

        }
        DrawSections(
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
                "Validate Path", aligner.ValidateTransformJson,
                "Apply Alignment", aligner.ApplyJsonRotationAndSyncCollider);
        }
    }
}

