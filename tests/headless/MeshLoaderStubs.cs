// Engine doubles exercise the actual loader's cache/ownership logic, not PhysX.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object
    {
        public string name = "test";
        public HideFlags hideFlags;
        public bool destroyed;
        public static void Destroy(Object o) { DestroyImmediate(o); }
        public static void DestroyImmediate(Object o) { if (!ReferenceEquals(o,null)) o.destroyed=true; }
        public static bool operator ==(Object a,Object b) { bool an=ReferenceEquals(a,null)||(!ReferenceEquals(a,null)&&a.destroyed); bool bn=ReferenceEquals(b,null)||(!ReferenceEquals(b,null)&&b.destroyed); return an||bn ? an&&bn : ReferenceEquals(a,b); }
        public static bool operator !=(Object a,Object b) { return !(a==b); }
        public override bool Equals(object o) { return ReferenceEquals(this,o); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
    }
    public class MonoBehaviour:Object { public GameObject gameObject=new GameObject(); }
    public class GameObject:Object
    {
        private readonly Dictionary<Type,object> components=new Dictionary<Type,object>();
        public T GetComponent<T>() where T:class { object v; return components.TryGetValue(typeof(T),out v)?(T)v:null; }
        public T AddComponent<T>() where T:class,new() { T v=new T(); components[typeof(T)]=v; return v; }
    }
    public class Material:Object { }
    public class MeshFilter:Object { public Mesh sharedMesh; }
    public class MeshRenderer:Object { public Material sharedMaterial; }
    public class MeshCollider:Object { public Mesh sharedMesh; public bool convex; }
    public class Mesh:Object
    {
        public Rendering.IndexFormat indexFormat; public int vertexCount; public int subMeshCount=1; private int indexCount;
        public void SetVertices(List<Vector3> v) { vertexCount=v.Count; }
        public void SetUVs(int i,List<Vector2> v) { }
        public void SetTriangles(List<int> v,int i,bool bounds) { indexCount=v.Count; }
        public void SetNormals(List<Vector3> v) { }
        public void RecalculateNormals() { }
        public void RecalculateBounds() { }
        public uint GetIndexCount(int i) { return (uint)indexCount; }
    }
    public struct Vector3 { public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;} public static Vector3 zero=>new Vector3(0,0,0); }
    public struct Vector2 { public float x,y; public Vector2(float a,float b){x=a;y=b;} public static Vector2 zero=>new Vector2(0,0); }
    [Flags] public enum HideFlags { None=0, HideAndDontSave=61 }
    public static class Application { public static bool isPlaying; }
    public static class Debug
    {
        public static readonly List<string> errors=new List<string>();
        public static void Log(object v,Object context) { }
        public static void LogError(object v,Object context) { errors.Add(v.ToString()); }
    }
    public sealed class Header:Attribute { public Header(string v){} }
    public sealed class SerializeField:Attribute { }
    public sealed class HideInInspector:Attribute { }
    public sealed class ContextMenu:Attribute { public ContextMenu(string v){} }
}
namespace UnityEngine.Rendering { public enum IndexFormat { UInt16,UInt32 } }
namespace UnityEngine.Serialization { public sealed class FormerlySerializedAsAttribute:Attribute { public FormerlySerializedAsAttribute(string v){} } }
namespace UnityEngine.Profiling { public static class Profiler { public static void BeginSample(string n){} public static void EndSample(){} } }
namespace GaussianSplatting.Runtime
{
    public static class ProjectPathResolver
    {
        public enum PathRoot { ProjectRoot,StreamingAssets }
        public static bool TryResolveConfiguredPath(string p,PathRoot r,bool shared,string json,string key,out string resolved,out string error) { resolved=p;error=null;return true; }
        public static bool TryResolvePath(string p,PathRoot r,out string resolved,out string error) { resolved=p;error=null;return true; }
        public static bool TryGetUnityProjectRelativePath(string p,out string relative) { relative=p;return p.StartsWith("Assets/",StringComparison.Ordinal); }
    }
}
#if UNITY_EDITOR
namespace UnityEditor
{
    public static class AssetDatabase
    {
        public static UnityEngine.Mesh asset;
        public static string dependency="v1";
        public static UnityEngine.Object LoadMainAssetAtPath(string p) { return asset; }
        public static T LoadAssetAtPath<T>(string p) where T:class { return asset as T; }
        public static UnityEngine.Object[] LoadAllAssetsAtPath(string p) { return new UnityEngine.Object[]{asset}; }
        public static bool Contains(UnityEngine.Object v) { return v!=null&&v==asset; }
        public static object GetAssetDependencyHash(string p) { return dependency; }
    }
}
#endif
