// Compilation/safety-control doubles only. Never used to collect paper measurements.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public enum HideFlags { HideAndDontSave }
    public enum LogType { Error, Assert, Warning, Log, Exception }
    public class Object
    {
        public string name;
        public HideFlags hideFlags;
        public static void DestroyImmediate(Object value) { }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : class { return gameObject.GetComponent<T>(); }
        public T[] GetComponentsInChildren<T>(bool inactive) { return new T[0]; }
    }
    public class MonoBehaviour : Component { }
    public struct Scene
    {
        public string path;
        public bool IsValid() { return true; }
    }
    public class GameObject : Object
    {
        public Transform transform;
        public Scene scene;
        public bool activeInHierarchy = true;
        private readonly Dictionary<Type, Component> components = new Dictionary<Type, Component>();
        public GameObject(string label = "stub") { name = label; transform = new Transform { gameObject = this, name = label }; }
        public void SetActive(bool value) { activeInHierarchy = value; }
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this }; components[typeof(T)] = c; return c; }
        public T GetComponent<T>() where T : class { Component c; return components.TryGetValue(typeof(T), out c) ? c as T : null; }
        public T[] GetComponentsInChildren<T>(bool inactive) { return new T[0]; }
    }
    public class Transform : Component
    {
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale = new Vector3(1, 1, 1);
        public bool hasChanged;
        public Vector3 position { get { return localPosition; } set { localPosition = value; } }
        public Matrix4x4 localToWorldMatrix;
        public void SetParent(Transform value, bool worldPositionStays) { parent = value; }
        public Vector3 TransformVector(Vector3 v) { return new Vector3(v.x * localScale.x, v.y * localScale.y, v.z * localScale.z); }
        public Vector3 InverseTransformPoint(Vector3 v) { return v; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public float magnitude { get { return (float)Math.Sqrt(x*x+y*y+z*z); } }
        public static Vector3 operator *(Vector3 v, float f) { return new Vector3(v.x*f,v.y*f,v.z*f); }
        public static Vector3 operator /(Vector3 v, float f) { return v*(1/f); }
    }
    public struct Quaternion
    {
        public float x,y,z,w;
        public static Quaternion Euler(Vector3 value) { return new Quaternion { x=value.x, y=value.y, z=value.z, w=1 }; }
        public static Quaternion operator *(Quaternion a, Quaternion b) { return new Quaternion { x=a.x+b.x, y=a.y+b.y, z=a.z+b.z, w=1 }; }
    }
    public struct Matrix4x4 { public float this[int index] { get { return 0; } } }
    public class Material : Object { }
    public class LineRenderer : Component { public Material sharedMaterial; }
    public static class Resources { public static T[] FindObjectsOfTypeAll<T>() { return new T[0]; } }
    public static class Application { public static string unityVersion="test double", dataPath="."; }
    public sealed class Logger { public LogType filterLogType; }
    public static class Debug
    {
        public static Logger unityLogger=new Logger();
        public static void Log(object value) { }
        public static void LogException(Exception error) { }
    }
}
namespace UnityEditor
{
    public sealed class MenuItem : Attribute { public MenuItem(string name) { } }
    public static class EditorApplication { public static bool isPlaying, isPaused; }
    public static class Selection { public static UnityEngine.GameObject activeGameObject; }
    public static class EditorUtility
    {
        public static string OpenFolderPanel(string a,string b,string c) { return ""; }
        public static void RevealInFinder(string path) { }
        public static bool DisplayCancelableProgressBar(string title,string detail,float progress) { return false; }
        public static void ClearProgressBar() { }
    }
}
public class JsonSpawner : UnityEngine.MonoBehaviour
{
    public string saveFilePath, transformJsonPath, geoJsonPath;
    public UnityEngine.Transform mapTransform;
    public UnityEngine.GameObject nodePrefab;
    public List<PrefabEntry> prefabEntries;
    public void SpawnFromJson() { }
    public struct Vector3Double { public double lon,lat,alt; }
    public Vector3Double ColmapToWgs84(UnityEngine.Vector3 p) { return new Vector3Double(); }
}
public class FloatingIcon : UnityEngine.MonoBehaviour
{
    public float floatAmplitude;
    public UnityEngine.Transform mainMap;
    public bool IsBeingManipulated;
}
public class PointData : UnityEngine.MonoBehaviour { }
public class RuntimeLine : UnityEngine.MonoBehaviour { public bool IsDraft; public int NodeCount; }
