// Engine doubles exercise production persistence and edit lifecycle control.
// Coordinate values here are synthetic; native Unity measurements stay separate.
#pragma warning disable 0649
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace UnityEngine
{
    public class Object { }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : class { return gameObject.GetComponent<T>(); }
    }
    public class MonoBehaviour : Component
    {
        public Coroutine StartCoroutine(IEnumerator routine) { return new Coroutine(); }
        public void StopCoroutine(Coroutine coroutine) { }
        public static T FindFirstObjectByType<T>() where T : class { return null; }
    }
    public class GameObject : Object
    {
        public string name = "fixture";
        public Transform transform;
        private readonly Dictionary<Type, Component> components = new Dictionary<Type, Component>();
        public GameObject() { transform = new Transform { gameObject = this }; }
        public T AddComponent<T>() where T : Component, new()
        { T value = new T { gameObject = this }; components[typeof(T)] = value; return value; }
        public T GetComponent<T>() where T : class
        { foreach (var value in components.Values) if (value is T) return value as T; return null; }
    }
    public class Transform : Component
    {
        public Transform parent;
        public Vector3 localPosition;
        public Vector3 position { get { return parent == null ? localPosition : parent.TransformPoint(localPosition); }
            set { localPosition = parent == null ? value : parent.InverseTransformPoint(value); } }
        public Vector3 TransformPoint(Vector3 point) { return localPosition + point; }
        public Vector3 InverseTransformPoint(Vector3 point) { return point - localPosition; }
        public void LookAt(Transform target) { }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
    }
    public class Coroutine { }
    public class Camera : Component { public static Camera main; }
    public class WaitForSeconds { public WaitForSeconds(float seconds) { } }
    public static class Time { public static float time, unscaledTime; }
    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public static float Sin(float x) { return (float)Math.Sin(x); }
        public static float Max(float a, float b) { return Math.Max(a,b); }
    }
    public sealed class Header : Attribute { public Header(string value) { } }
    public sealed class Tooltip : Attribute { public Tooltip(string value) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class Min : Attribute { public Min(float value) { } }
    public static class Application { public static string dataPath, persistentDataPath; public static bool isPlaying; }
    public static class Debug
    {
        public static void Log(object value) { }
        public static void LogWarning(object value, object context = null) { }
        public static void LogError(object value) { }
    }
    public struct Color { }
    public static class ColorUtility { public static bool TryParseHtmlString(string s, out Color c) { c=new Color(); return true; } }
}
namespace UnityEngine.XR.Interaction.Toolkit
{
    public class SelectEnterEventArgs { }
    public class SelectExitEventArgs { }
    public class HoverEnterEventArgs { }
    public class TestEvent<T> { public void AddListener(Action<T> a) { } public void RemoveListener(Action<T> a) { } }
}
namespace UnityEngine.XR.Interaction.Toolkit.Interactables
{
    using UnityEngine.XR.Interaction.Toolkit;
    public class XRBaseInteractable : UnityEngine.Component
    {
        public TestEvent<SelectEnterEventArgs> selectEntered = new TestEvent<SelectEnterEventArgs>();
        public TestEvent<SelectExitEventArgs> selectExited = new TestEvent<SelectExitEventArgs>();
        public TestEvent<HoverEnterEventArgs> hoverEntered = new TestEvent<HoverEnterEventArgs>();
    }
    public class XRGrabInteractable : XRBaseInteractable { public bool isSelected; }
}
public class PointData : UnityEngine.Component
{
    public string pointID, pointClass, heightSource, heightStatus;
    public double latitude, longitude, altitude, heightAboveSurface;
    public bool hasHeightAboveSurface, elevationUnresolved;
    public void SetElevationUnresolved(bool value) { elevationUnresolved = value; }
    public void NotifyDataChanged() { }
}
public enum MarkerFocusReason { Hover, Select }
public interface IMarkerFocusable { PointData MarkerData { get; } void FocusMarker(MarkerFocusReason reason); }
public static class MarkerEventManager
{
    public static void RaiseMarkerMoved(PointData data) { }
    public static void RaiseMarkerHovered(PointData data) { }
    public static void RaiseMarkerSelected(PointData data) { }
}
public class PointInfoPanel
{
    public void OnMarkerSelected(PointData data) { }
    public bool IsObservingMarker(PointData data) { return false; }
    public void LiveUpdateIfCurrent(PointData data) { }
}
public partial class JsonSpawner : UnityEngine.MonoBehaviour
{
    public UnityEngine.Transform mapTransform;
    public PointInfoPanel infoPanel;
    public string saveFilePath, resolvedSaveFilePath;
    public bool saveInsideProjectFolder, logSaveStageTimings;
    private Root currentRootData;
    private int dataVersion, lastSavedVersion;
    private bool saveQueued, saveInProgress;
    private float autoSaveTimer;
    public float AutoSaveTimer { get { return autoSaveTimer; } }
    private GeoJsonSaveSequence diskSaves = new GeoJsonSaveSequence();
    private object fileWriteLock { get { return diskSaves.SyncRoot; } }
    public bool HasPendingLocalPersistence { get { return dataVersion > lastSavedVersion || saveInProgress || saveQueued; } }
    public int conversions, saves;
    public string LastValidationError;
    private sealed class SaveStageTimings { public double outbox, labels, serialize; }
    private sealed class NodeMapping
    {
        public UnityEngine.Transform node, parentCentroid, editStartParent;
        public JArray coordArray;
        public Feature parentFeature;
        public bool isCentroid, editInProgress;
        public UnityEngine.Vector3 editStartLocal, editStartMapLocal;
        public Vector3Double editStartGeographic;
    }
    private List<NodeMapping> nodeMappings = new List<NodeMapping>();
    public struct Vector3Double { public double lon, lat, alt; }
    public void Fixture(Root root, string path) { currentRootData = root; saveFilePath = path; }
    public void Bind(UnityEngine.Transform node, Feature feature, JArray coordinate, bool centroid = false)
    { nodeMappings.Add(new NodeMapping { node=node, parentFeature=feature, coordArray=coordinate, isCentroid=centroid }); }
    public void FlushFixture() { MarkDataDirty(); FlushPendingSaveSynchronously(); saves++; }
    public Vector3Double ColmapToWgs84(UnityEngine.Vector3 local)
    { conversions++; return new Vector3Double { lon=local.x, lat=local.y, alt=local.z }; }
    public bool RefreshLiveHeightAboveSurface(PointData data, UnityEngine.Transform node)
    {
        var map = mapTransform != null ? mapTransform : transform;
        Vector3Double geo = ColmapToWgs84(map.InverseTransformPoint(node.position));
        data.longitude=geo.lon; data.latitude=geo.lat; data.altitude=geo.alt;
        data.heightSource="unavailable"; data.heightStatus="no_surface"; return false;
    }
    private UnityEngine.Vector3 GetColmapPosition(double lon, double lat, double alt)
    { return new UnityEngine.Vector3((float)lon,(float)lat,(float)alt); }
    private static void SyncPointDataFromWgs(UnityEngine.Transform node, Vector3Double geo)
    { var data=node.GetComponent<PointData>(); data.longitude=geo.lon; data.latitude=geo.lat; data.altitude=geo.alt; }
    private static bool IsPointFeature(Feature feature) { return feature.geometry.type == "Point"; }
    private static bool SupportsPerVertexHeight(Feature feature) { return feature.geometry.type == "LineString" || feature.geometry.type == "Polygon"; }
    private static void StorePointHeightMetadata(Feature feature, PointData data)
    { feature.properties.heightSource=data.heightSource; feature.properties.heightStatus=data.heightStatus; }
    private void RecalculateCentroidForNode(NodeMapping mapping) { }
    private static bool IsValidCoordinateSet(double lat, double lon, double alt) { return true; }
    private static bool TryExtractFirstCoordinateTriplet(JToken token, out double lon, out double lat, out double alt)
    { lon=1;lat=1;alt=1;return true; }
    private static double MeasureSaveStage(string name, Action action) { action(); return 0; }
    private void ReportValidationError(string error) { LastValidationError=error; }
    private void UpdateSyncStatusLabels() { }
    private static string SanitizeSaveFilePath(string path) { return path; }
    private static string ResolveSavePathForRuntime(string path, bool inside) { return path; }
}
