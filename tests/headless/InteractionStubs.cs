// Engine doubles verify panel selection and synchronous collider-state handling.
// They do not simulate PhysX intersections, XR input, or rendered TMP layout.
using System;
using System.Collections;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object
    {
        private static int nextId;
        private readonly int id=++nextId;
        public string name="test";
        public int GetInstanceID() { return id; }
        public static void Destroy(Object obj) { }
    }
    public class Component:Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T:class { return gameObject.GetComponent<T>(); }
        public T GetComponentInParent<T>() where T:class { return GetComponent<T>(); }
    }
    public class GameObject:Object
    {
        private readonly Dictionary<Type,Component> components=new Dictionary<Type,Component>();
        public bool activeSelf=true;
        public bool activeInHierarchy { get { return activeSelf; } }
        public Transform transform;
        public GameObject() { transform=new Transform { gameObject=this }; }
        public T GetComponent<T>() where T:class
        {
            foreach(Component c in components.Values) if(c is T) return (T)(object)c;
            return null;
        }
        public T AddComponent<T>() where T:Component
        {
            T component=(T)Activator.CreateInstance(typeof(T),true); component.gameObject=this; components[typeof(T)]=component; return component;
        }
        public void SetActive(bool value) { activeSelf=value; }
    }
    public class MonoBehaviour:Component
    {
        public Coroutine StartCoroutine(IEnumerator routine) { Run(routine); return new Coroutine(); }
        private static void Run(IEnumerator routine)
        {
            int budget=1000;
            while(routine.MoveNext())
            {
                if(--budget==0) throw new Exception("Test coroutine did not finish.");
                IEnumerator child=routine.Current as IEnumerator; if(child!=null) Run(child);
            }
        }
        public void StopCoroutine(Coroutine routine) { }
    }
    public class Transform:Component { public Matrix4x4 localToWorldMatrix; }
    public class Coroutine { }
    public class Sprite:Object { }
    public class CanvasGroup:Component { public float alpha; }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0,1,0); } }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
        public static Vector3 operator +(Vector3 a,Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x,-a.y,-a.z); }
        public static Vector3 operator *(Vector3 a,float b) { return new Vector3(a.x*b,a.y*b,a.z*b); }
        public static Vector3 operator /(Vector3 a,float b) { return a*(1f/b); }
    }
    public struct Matrix4x4
    {
        public float determinant { get { return 1f; } }
        public Vector3 MultiplyVector(Vector3 value) { return value; }
    }
    public struct LayerMask { public static implicit operator LayerMask(int value) { return new LayerMask(); } }
    public struct Ray { public Vector3 origin,direction; public Ray(Vector3 origin,Vector3 direction) { this.origin=origin;this.direction=direction; } }
    public struct RaycastHit { public Vector3 point; public float distance; public Collider collider; }
    public class Collider:Component
    {
        private bool enabledValue=true;
        public int stateChanges;
        public bool enabled { get { return enabledValue; } set { if(enabledValue!=value) stateChanges++; enabledValue=value; } }
        public Func<Ray,float,RaycastHit?> query;
        public bool Raycast(Ray ray,out RaycastHit hit,float distance)
        {
            if(!enabled || !gameObject.activeInHierarchy) throw new Exception("Raycast attempted on an unavailable collider.");
            RaycastHit? result=query==null?null:query(ray,distance); hit=result??new RaycastHit(); return result.HasValue;
        }
    }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics
    {
        public static int syncCount;
        public static RaycastHit[] hits=new RaycastHit[0];
        public static void SyncTransforms() { syncCount++; }
        public static RaycastHit[] RaycastAll(Ray ray,float distance,LayerMask mask,QueryTriggerInteraction trigger) { return (RaycastHit[])hits.Clone(); }
    }
    public struct Color { public static void RGBToHSV(Color value,out float h,out float s,out float v) { h=s=v=0f; } }
    public static class ColorUtility { public static bool TryParseHtmlString(string s,out Color color) { color=new Color();return false; } }
    public static class Mathf
    {
        public static float Abs(float v) { return Math.Abs(v); }
        public static float Clamp01(float v) { return Math.Max(0f,Math.Min(1f,v)); }
        public static int Clamp(int v,int min,int max) { return Math.Max(min,Math.Min(max,v)); }
        public static float Lerp(float a,float b,float t) { return a+(b-a)*t; }
    }
    public static class Time { public static float deltaTime=1f; }
    public static class Debug { public static void Log(object v) { } public static void LogWarning(object v) { } public static void LogWarning(object v,Object context) { } }
    public sealed class Header:Attribute { public Header(string s) { } }
    public sealed class Tooltip:Attribute { public Tooltip(string s) { } }
    public sealed class SerializeField:Attribute { }
    public sealed class Min:Attribute { public Min(float v) { } }
}
namespace TMPro { public class TextMeshProUGUI:UnityEngine.Component { public string text; } }
namespace UnityEngine.UI
{
    public class Slider:UnityEngine.Component { public float value; }
    public class Image:UnityEngine.Component { public UnityEngine.Sprite sprite; public bool enabled; }
}
public class PointData:UnityEngine.MonoBehaviour
{
    public string pointID,pointClass,source,syncStatus,behavior,category,lineColorHex;
    public bool elevationUnresolved,hasHeightAboveSurface;
    public string heightSource;
    public double heightAboveSurface,latitude,longitude;
    public float confidence;
    public Action OnDataChanged;
    public void NotifyDataChanged() { if(OnDataChanged!=null) OnDataChanged(); }
}
public class PulseEffect:UnityEngine.MonoBehaviour { public void StopPulse() { } public void StartPulse() { } }
public class DeleteConfirmButton:UnityEngine.MonoBehaviour { public TMPro.TextMeshProUGUI buttonText; public void SetTargetNode(UnityEngine.Transform node) { } }
public interface IAnnotationPersistenceService { bool DeleteFeatureByNode(UnityEngine.Transform node,bool requireDrawMode=false); }
