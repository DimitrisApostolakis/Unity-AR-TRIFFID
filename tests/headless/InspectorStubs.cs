// Minimal Editor doubles: exercise field grouping/search without launching Unity.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { }
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute { public readonly string header; public HeaderAttribute(string text) { header=text; } }
    public class GUIContent { public GUIContent(string text) { } }
    public static class GUILayout
    {
        public static readonly HashSet<string> clicks = new HashSet<string>();
        public static bool Button(string text) { return clicks.Remove(text); }
    }
}
namespace UnityEditor
{
    public class Editor
    {
        public UnityEngine.Object target;
        public UnityEngine.Object[] targets = new UnityEngine.Object[1];
        public SerializedObject serializedObject;
        public virtual void OnInspectorGUI() { }
    }
    public sealed class SerializedProperty
    {
        public string propertyPath, name, displayName, tooltip;
        public bool boolValue, hasMultipleDifferentValues;
        internal List<SerializedProperty> source;
        private int position=-1;
        public bool NextVisible(bool enterChildren)
        {
            if (++position >= source.Count) return false;
            var p=source[position];propertyPath=p.propertyPath;name=p.name;displayName=p.displayName;tooltip=p.tooltip;
            return true;
        }
    }
    public sealed class SerializedObject
    {
        public readonly List<SerializedProperty> properties=new List<SerializedProperty>();
        public void Update() { }
        public void ApplyModifiedProperties() { }
        public SerializedProperty FindProperty(string path) { return properties.Find(p=>p.propertyPath==path); }
        public SerializedProperty GetIterator() { return new SerializedProperty { source=properties }; }
    }
    public static class SessionState
    {
        public static readonly Dictionary<string,bool> values=new Dictionary<string,bool>();
        public static bool GetBool(string key,bool fallback) { bool value;return values.TryGetValue(key,out value)?value:fallback; }
        public static void SetBool(string key,bool value) { values[key]=value; }
    }
    public enum MessageType { None,Info }
    public static class EditorGUI
    {
        public static int indentLevel;
        public static bool disabled;
        public sealed class DisabledScope : IDisposable
        {
            private readonly bool previous;
            public DisabledScope(bool value) { previous=disabled;disabled|=value; }
            public void Dispose() { disabled=previous; }
        }
        public sealed class IndentLevelScope : IDisposable { public void Dispose() { } }
    }
    public static class EditorGUILayout
    {
        public static string search=string.Empty;
        public static readonly List<string> drawn=new List<string>();
        public static readonly List<string> readOnly=new List<string>();
        public static void PropertyField(SerializedProperty p)
        { drawn.Add(p.propertyPath);if(EditorGUI.disabled)readOnly.Add(p.propertyPath); }
        public static void PropertyField(SerializedProperty p,bool children) { PropertyField(p); }
        public static void PropertyField(SerializedProperty p,UnityEngine.GUIContent label) { PropertyField(p); }
        public static void HelpBox(string text,MessageType kind) { }
        public static string TextField(string label,string value) { return search; }
        public static bool Foldout(bool value,string title,bool toggle) { return value; }
        public static void Space() { }
        public sealed class HorizontalScope : IDisposable { public void Dispose() { } }
    }
    public sealed class CustomEditor : Attribute { public CustomEditor(Type type) { } }
    public sealed class CanEditMultipleObjects : Attribute { }
}
public class JsonSpawner : UnityEngine.Object { }
public class GeoJsonSyncCoordinator : UnityEngine.Object { }
public class GeoJsonApiManager : UnityEngine.Object { }
public class MapColliderSync : UnityEngine.Object { public void SyncToMap() { } }
public class MeshPathJsonLoader : UnityEngine.Object { public void ValidatePaths() { } public void LoadAndApplyMesh() { } }
public class ColmapGaussianRotationAligner : UnityEngine.Object { public void ValidateTransformJson() { } public void ApplyJsonRotationAndSyncCollider() { } }
public class XRControllerLogger : UnityEngine.Object { }
public class PointInfoPanel : UnityEngine.Object { }
public class MenuButtonController : UnityEngine.Object { }
public class MenuStateRestorer : UnityEngine.Object { }
public class CheckboxFilterGroup : UnityEngine.Object { }
public class CheckboxFilterHook : UnityEngine.Object { }
public class FloatingIcon : UnityEngine.Object { }
public class PointData : UnityEngine.Object { }
public class FreeLocomotion : UnityEngine.Object { }
public class XRControllerInputBridge : UnityEngine.Object { }
public class MapReset : UnityEngine.Object { }
public class ScaleChanger : UnityEngine.Object { }
public class LineWidthController : UnityEngine.Object { }
public class CameraBackgroundToggle : UnityEngine.Object { }
public class DeleteConfirmButton : UnityEngine.Object { }
public class TransformSliderControl : UnityEngine.Object { }
public class RainbowCircleImage : UnityEngine.Object { }
public class PulseEffect : UnityEngine.Object { }
public class MarkerInteractionRelay : UnityEngine.Object { }
