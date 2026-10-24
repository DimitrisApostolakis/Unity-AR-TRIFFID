using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class MeshLoaderRegressionTests
{
    private static int passed,failed;
    private const string Triangle="v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
    public static int Main()
    {
        Test("repeated surface activation reuses one mesh and collider assignment",f=>
        { f.Enable(); Mesh m=f.Mesh; for(int i=0;i<11;i++)f.Enable(); Check(f.loader.MeshLoadCount==1&&f.loader.MeshCacheHitCount==11&&f.loader.ColliderAssignmentCount==1&&f.Mesh==m&&m.vertexCount==3); });
        Test("enable and start do not load the same source twice",f=>
        { Set(f.loader,"loadOnStart",true);f.Enable();Invoke(f.loader,"Start");Check(f.loader.MeshLoadCount==1); });
        Test("explicit reload bypasses cache and releases previous runtime mesh",f=>
        { f.Enable();Mesh m=f.Mesh;f.loader.LoadAndApplyMesh();Check(f.loader.MeshLoadCount==2&&m==null&&f.Mesh!=null); });
        Test("source timestamp and content changes invalidate the cache",f=>
        { f.Enable();Mesh m=f.Mesh;DateTime stamp=File.GetLastWriteTimeUtc(f.path);File.WriteAllText(f.path,Triangle.Replace("v 1 0 0","v 2 0 0"));File.SetLastWriteTimeUtc(f.path,stamp.AddSeconds(2));f.Enable();Check(f.loader.MeshLoadCount==2&&f.loader.ColliderAssignmentCount==2&&m==null); });
        Test("source path change invalidates the cache",f=>
        { f.Enable();string p=Path.Combine(f.directory,"other.obj");File.WriteAllText(p,Triangle);Set(f.loader,"meshPath",p);f.Enable();Check(f.loader.MeshLoadCount==2); });
        Test("missing source reports failure and retains the last usable mesh",f=>
        { f.Enable();Mesh m=f.Mesh;File.Delete(f.path);Debug.errors.Clear();f.Enable();Check(f.loader.MeshLoadCount==1&&f.Mesh==m&&Debug.errors.Count>0); });
        Test("malformed replacement retains the last usable mesh",f=>
        { f.Enable();Mesh m=f.Mesh;File.WriteAllText(f.path,"invalid OBJ\n");f.Enable();Check(f.loader.MeshLoadCount==1&&f.Mesh==m); });
        Test("removed collider binding is restored without reparsing",f=>
        { f.Enable();Mesh m=f.Mesh;f.loader.gameObject.GetComponent<MeshCollider>().sharedMesh=null;f.Enable();Check(f.loader.MeshLoadCount==1&&f.loader.ColliderAssignmentCount==2&&f.Mesh==m); });
        Test("collider option change reapplies without reparsing",f=>
        { f.Enable();Set(f.loader,"convexCollider",true);f.Enable();Check(f.loader.MeshLoadCount==1&&f.loader.ColliderAssignmentCount==2&&f.loader.gameObject.GetComponent<MeshCollider>().convex); });
        Test("destruction releases owned mesh and clears its target bindings",f=>
        { f.Enable();Mesh m=f.Mesh;Invoke(f.loader,"OnDestroy");Check(m==null&&f.Mesh==null&&f.loader.gameObject.GetComponent<MeshCollider>().sharedMesh==null); });
#if UNITY_EDITOR
        Test("imported asset is reused without taking ownership",f=>
        { Mesh m=new Mesh();UnityEditor.AssetDatabase.asset=m;Set(f.loader,"meshPath","Assets/collider.obj");Set(f.loader,"meshLoadMode",MeshLoadMode.UnityEditorAssetPath);f.Enable();f.Enable();Invoke(f.loader,"OnDestroy");Check(f.loader.MeshLoadCount==1&&m!=null); });
        Test("imported asset dependency changes invalidate the cache",f=>
        { UnityEditor.AssetDatabase.asset=new Mesh();UnityEditor.AssetDatabase.dependency="before";Set(f.loader,"meshPath","Assets/collider.obj");Set(f.loader,"meshLoadMode",MeshLoadMode.UnityEditorAssetPath);f.Enable();UnityEditor.AssetDatabase.dependency="after";f.Enable();Check(f.loader.MeshLoadCount==2&&f.loader.ColliderAssignmentCount==2); });
#endif
        Console.WriteLine("MESH LOADER RESULT: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
    }
    private sealed class Fixture:IDisposable
    {
        public readonly string directory,path;
        public readonly MeshPathJsonLoader loader=new MeshPathJsonLoader();
        public Mesh Mesh=>loader.gameObject.GetComponent<MeshFilter>()?.sharedMesh;
        public Fixture()
        {
            directory=Path.Combine(Path.GetTempPath(),"ar2_mesh_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            path=Path.Combine(directory,"surface.obj");File.WriteAllText(path,Triangle);
            Set(loader,"meshPath",path);Set(loader,"meshLoadMode",MeshLoadMode.ExternalObjFile);Set(loader,"loadOnEnable",true);Set(loader,"logDetails",false);
        }
        public void Enable(){Invoke(loader,"OnEnable");}
        public void Dispose(){Invoke(loader,"OnDestroy");Directory.Delete(directory,true);}
    }
    private static void Set(object target,string name,object value){target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(target,value);}
    private static void Invoke(object target,string name){target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(target,null);}
    private static void Check(bool value){if(!value)throw new InvalidOperationException("Assertion failed");}
    private static void Test(string name,Action<Fixture> action)
    {try{using(Fixture f=new Fixture())action(f);passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
}
