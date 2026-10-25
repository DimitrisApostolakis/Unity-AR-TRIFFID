using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class InspectorRegressionTests
{
    private class BaseFixture : UnityEngine.Object
    {
        [Header("Inherited setup")] protected int inherited=1;
    }
    private sealed class Fixture : BaseFixture
    {
        [Header("References")] public int map=1;
        public int marker=2;
        [Header("Remote Polling")] public int interval=3;
        [Header("Runtime Diagnostics")] public int count=4;
    }
    private sealed class Inspector : TriffidGroupedEditor
    {
        public void DrawWithout(string property) { serializedObject.Update();DrawInspectorToolbar();DrawSections(property); }
    }
    private static int passed,failed;
    private static void Check(bool value) { if(!value)throw new Exception("Assertion failed"); }
    private static Inspector Create()
    {
        SessionState.values.Clear();EditorGUILayout.drawn.Clear();EditorGUILayout.readOnly.Clear();
        EditorGUILayout.search=string.Empty;GUILayout.clicks.Clear();
        var editor=new Inspector { target=new Fixture(),serializedObject=new SerializedObject() };
        foreach(string name in new[]{"m_Script","inherited","map","marker","interval","count"})
            editor.serializedObject.properties.Add(new SerializedProperty { propertyPath=name,name=name,displayName=name,tooltip=name=="interval"?"Seconds between server checks":string.Empty });
        return editor;
    }
    private static void Test(string name,Action body)
    { try { body();passed++;Console.WriteLine("PASS "+name); } catch(Exception e) { failed++;Console.WriteLine("FAIL "+name+": "+e.Message); } }
    public static int Main()
    {
        Test("expand all exposes every original top-level property exactly once",()=>{
            var e=Create();GUILayout.clicks.Add("Expand all");e.OnInspectorGUI();
            Check(EditorGUILayout.drawn.Count==6 && new HashSet<string>(EditorGUILayout.drawn).Count==6);
        });
        Test("headers on inherited fields are recognized and first section starts open",()=>{
            var e=Create();e.OnInspectorGUI();Check(EditorGUILayout.drawn.Contains("inherited") && !EditorGUILayout.drawn.Contains("map"));
        });
        Test("collapsed state persists through another inspector redraw",()=>{
            var e=Create();GUILayout.clicks.Add("Collapse all");e.OnInspectorGUI();EditorGUILayout.drawn.Clear();e.OnInspectorGUI();
            Check(EditorGUILayout.drawn.Count==1 && EditorGUILayout.drawn[0]=="m_Script");
        });
        Test("search finds tooltip matches in a collapsed section without changing its saved state",()=>{
            var e=Create();GUILayout.clicks.Add("Collapse all");e.OnInspectorGUI();EditorGUILayout.drawn.Clear();
            EditorGUILayout.search="server checks";e.OnInspectorGUI();Check(EditorGUILayout.drawn.Contains("interval") && !EditorGUILayout.drawn.Contains("map"));
            EditorGUILayout.drawn.Clear();EditorGUILayout.search=string.Empty;e.OnInspectorGUI();Check(EditorGUILayout.drawn.Count==1);
        });
        Test("diagnostic counters and script reference are read-only while settings remain editable",()=>{
            var e=Create();GUILayout.clicks.Add("Expand all");e.OnInspectorGUI();
            Check(EditorGUILayout.readOnly.Contains("count") && EditorGUILayout.readOnly.Contains("m_Script") && !EditorGUILayout.readOnly.Contains("map"));
        });
        Test("dedicated path UI can exclude properties without dropping unrelated settings",()=>{
            var e=Create();GUILayout.clicks.Add("Expand all");e.DrawWithout("map");
            Check(!EditorGUILayout.drawn.Contains("map") && EditorGUILayout.drawn.Contains("marker"));
        });
        Console.WriteLine("INSPECTOR RESULT: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
    }
}
