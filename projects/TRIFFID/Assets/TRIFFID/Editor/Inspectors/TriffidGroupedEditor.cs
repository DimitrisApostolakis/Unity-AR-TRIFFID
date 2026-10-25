using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only presentation of existing serialized fields. No runtime settings
/// are copied, renamed or hidden behind a second configuration object.
/// </summary>
public abstract class TriffidGroupedEditor : Editor
{
    private sealed class Section
    {
        public string title;
        public readonly List<string> properties = new List<string>();
    }

    private string search = string.Empty;
    protected virtual string Description => string.Empty;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawInspectorToolbar();
        DrawSections();
        serializedObject.ApplyModifiedProperties();
        DrawActions();
    }

    protected virtual void DrawActions() { }

    protected void DrawInspectorToolbar()
    {
        using (new EditorGUI.DisabledScope(true))
        {
            SerializedProperty script = serializedObject.FindProperty("m_Script");
            if (script != null) EditorGUILayout.PropertyField(script);
        }
        if (!string.IsNullOrEmpty(Description))
            EditorGUILayout.HelpBox(Description, MessageType.Info);
        search = EditorGUILayout.TextField("Find setting", search);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Expand all")) SetAllSections(true);
            if (GUILayout.Button("Collapse all")) SetAllSections(false);
        }
        EditorGUILayout.Space();
    }

    protected bool DrawSectionHeading(string title, bool initiallyOpen = false)
    {
        string key = SectionKey(title);
        bool expanded = SessionState.GetBool(key, initiallyOpen);
        bool value = EditorGUILayout.Foldout(expanded || search.Length > 0, title, true);
        if (search.Length == 0 && value != expanded) SessionState.SetBool(key, value);
        return value;
    }

    protected void DrawSections(params string[] excludedProperties)
    {
        var excluded = new HashSet<string>(excludedProperties);
        List<Section> sections = CollectSections();
        for (int i = 0; i < sections.Count; i++)
        {
            Section section = sections[i];
            var visible = new List<SerializedProperty>();
            foreach (string path in section.properties)
            {
                if (excluded.Contains(path)) continue;
                SerializedProperty property = serializedObject.FindProperty(path);
                if (property != null && MatchesSearch(section.title, property)) visible.Add(property);
            }
            if (visible.Count == 0) continue;
            if (DrawSectionHeading(section.title, i == 0))
            {
                using (new EditorGUI.IndentLevelScope())
                using (new EditorGUI.DisabledScope(section.title.IndexOf("Diagnostics", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    foreach (SerializedProperty property in visible)
                        EditorGUILayout.PropertyField(property, true);
                }
            }
            EditorGUILayout.Space();
        }
    }

    private bool MatchesSearch(string section, SerializedProperty property)
    {
        return search.Length == 0 || Contains(section, search) || Contains(property.displayName, search) ||
               Contains(property.name, search) || Contains(property.tooltip, search);
    }

    private static bool Contains(string value, string query)
    {
        return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private List<Section> CollectSections()
    {
        var result = new List<Section>();
        var byTitle = new Dictionary<string, Section>();
        string title = "Settings";
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.propertyPath == "m_Script") continue;
            FieldInfo field = FindField(target.GetType(), iterator.name);
            if (field != null)
            {
                var header = Attribute.GetCustomAttribute(field, typeof(HeaderAttribute)) as HeaderAttribute;
                if (header != null) title = header.header;
            }
            Section section;
            if (!byTitle.TryGetValue(title, out section))
            {
                section = new Section { title = title };
                byTitle.Add(title, section);
                result.Add(section);
            }
            section.properties.Add(iterator.propertyPath);
        }
        return result;
    }

    private static FieldInfo FindField(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private string SectionKey(string title) => "TRIFFID.Inspector." + target.GetType().FullName + "." + title;

    private void SetAllSections(bool expanded)
    {
        foreach (Section section in CollectSections()) SessionState.SetBool(SectionKey(section.title), expanded);
        // Path editors draw this section with a dedicated, shared path-source UI.
        SessionState.SetBool(SectionKey("Path source"), expanded);
    }
}
