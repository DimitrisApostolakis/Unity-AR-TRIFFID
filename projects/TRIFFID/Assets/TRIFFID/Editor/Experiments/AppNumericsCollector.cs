using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Native measurements only. The host application is read, never used as a fixture.
public static class AppNumericsCollector
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    public const string HeightHeader = "calibration,pose,scope,surface,collider_enabled,east,north,expected_hit,expected_height_m,actual_hit,actual_height_m,height_source,height_status,collider_restored,status_correct,error_m";

    [MenuItem("Tools/Experiments/App Numerics/Run Height Geometry and Core Checks")]
    private static void ChooseAndRun()
    {
        try
        {
            JsonSpawner source = Selected();
            string folder = EditorUtility.OpenFolderPanel("Choose a parent folder for the numerical capture", "", "");
            if (string.IsNullOrEmpty(folder)) return;
            string capture = Run(folder, source);
            Debug.Log("[AppNumerics] Native batch completed. Retain failures and stress limitations. Capture: " + capture);
            EditorUtility.RevealInFinder(capture);
        }
        catch (Exception error) { Debug.LogException(error); }
    }

    internal static JsonSpawner Selected()
    {
        if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
            throw new InvalidOperationException("For this numerical batch, start Play, let saving settle, then PAUSE and select CoordinatesLogic.");
        JsonSpawner source = Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponent<JsonSpawner>();
        if (source == null) throw new InvalidOperationException("Select the loaded JsonSpawner GameObject.");
        if ((bool)Get(source,"saveInProgress") || (bool)Get(source,"saveQueued") ||
            (int)Get(source,"dataVersion") != (int)Get(source,"lastSavedVersion") || source.HasActiveAnnotationDraft)
            throw new InvalidOperationException("Unpause, finish/cancel drawing, release markers and let saves settle; then pause again.");
        if (!(Get(source,"currentRootData") is Root) || !(Get(source,"transformData") is TransformConfig))
            throw new InvalidOperationException("Load the normal scene and its alignment first.");
        return source;
    }

    public static string Run(string output, JsonSpawner source)
    {
        if (!EditorApplication.isPlaying || !EditorApplication.isPaused || source == null)
            throw new InvalidOperationException("Native numerical capture requires paused Play mode and an explicit JsonSpawner.");
        string sourceData = JsonConvert.SerializeObject(Get(source,"currentRootData"));
        string sourceFile = File.Exists(source.saveFilePath) ? Hash(source.saveFilePath) : null;
        Transform map = source.mapTransform != null ? source.mapTransform : source.transform;
        string mapMatrix = MatrixText(map);
        string capture = NewCapture(output,"AppNumerics");
        JObject meta = Metadata("height_geometry_core",source);
        meta["height_budget_m"] = 0.005;
        meta["height_budget_status"] = "Predeclared synthetic pilot budget; not geographic or mission accuracy.";
        meta["limits"] = "Synthetic analytic surfaces and deterministic core checks. No real HTTP fault test, human interaction, survey accuracy or rendering benchmark.";
        meta["source_save_sha256_before"] = sourceFile;
        try
        {
            TransformConfig site = JsonConvert.DeserializeObject<TransformConfig>(JsonConvert.SerializeObject(Get(source,"transformData")));
            Write(capture,"alignment.json",JObject.FromObject(site));
            TransformConfig identity = new TransformConfig {
                origin_wgs84 = new OriginWgs84 {lon=23.7,lat=38,alt=100},
                colmap_to_enu = new ColmapToEnu {scale=1,R_rowmajor=new[]{1f,0,0,0,1,0,0,0,1},t=new[]{0f,0,0}} };
            var matrices = new JArray();
            using (var csv = new StreamWriter(Path.Combine(capture,"height_raw.csv")))
            {
                csv.WriteLine(HeightHeader);
                Heights(csv,"identity",identity,matrices);
                Heights(csv,"site",site,matrices);
            }
            Write(capture,"height_poses.json",matrices);
            Write(capture,"polygons.json",Polygons());
            TextWriter previous = Console.Out;
            using (var log = new StringWriter(CI))
            {
                try { Console.SetOut(log); meta["core_result_code"] = AR2CoreRegressionTests.Run(); }
                finally { Console.SetOut(previous); File.WriteAllText(Path.Combine(capture,"core_checks.txt"),log.ToString()); }
            }
            meta["core_scope"] = "Production deterministic state/serialization/atomic-file checks in native Unity; fake server cases do not measure real HTTP or process-kill recovery.";
            meta["completed"] = true;
        }
        catch (Exception error) { meta["error"] = error.ToString(); throw; }
        finally
        {
            EditorUtility.ClearProgressBar();
            meta["source_state_restored"] = sourceData == JsonConvert.SerializeObject(Get(source,"currentRootData")) &&
                sourceFile == (File.Exists(source.saveFilePath) ? Hash(source.saveFilePath) : null) &&
                mapMatrix == MatrixText(map);
            meta["source_save_sha256_after"] = File.Exists(source.saveFilePath) ? Hash(source.saveFilePath) : null;
            Finish(capture,meta);
        }
        return capture;
    }

    private static void Heights(StreamWriter csv, string profile, TransformConfig config, JArray matrices)
    {
        // Invert the linear calibration to construct known ENU surfaces. The
        // oracle height is analytic ENU up, independent of the query's ray axis.
        float[] r = config.colmap_to_enu.R_rowmajor;
        Matrix4x4 linear = Matrix4x4.identity;
        for (int i=0;i<3;i++) for(int j=0;j<3;j++) linear[i,j]=config.colmap_to_enu.scale*r[3*i+j];
        if (Math.Abs(linear.determinant)<1e-12f) throw new InvalidDataException("Singular reference calibration.");
        Matrix4x4 inverse = linear.inverse;
        var root = new GameObject("AppNumerics owned surface fixture");
        root.hideFlags = HideFlags.HideAndDontSave;
        GameObject map = Child(root,"map"), host = Child(map,"inactive spawner"), marker = Child(map,"marker");
        host.SetActive(false);
        JsonSpawner fixture = host.AddComponent<JsonSpawner>(); fixture.mapTransform=map.transform;
        Set(fixture,"transformData",config); Set(fixture,"autoActivateSurfaceForSnap",false);
        Set(fixture,"surfaceSnapRayStartOffset",10f); Set(fixture,"surfaceSnapRayDistance",100f);
        PointData data = marker.AddComponent<PointData>();
        try
        {
            foreach (int pose in Enumerable.Range(0,6))
            {
                root.transform.SetPositionAndRotation(pose==5 ? new Vector3(10000,10000,-10000) : pose==0 ? Vector3.zero : new Vector3(3*pose,4,-2*pose),
                    pose==0 ? Quaternion.identity : Quaternion.Euler(11*pose,-19,7));
                root.transform.localScale = pose==4 ? new Vector3(.8f,1.2f,.9f) : Vector3.one;
                map.transform.localPosition = pose==0 ? Vector3.zero : new Vector3(2,-1,3);
                map.transform.localRotation = pose==0 ? Quaternion.identity : Quaternion.Euler(23,41*pose,-13);
                Vector3[] scales={Vector3.one,Vector3.one*.01f,Vector3.one*3,new Vector3(-1,1,1),new Vector3(-.4f,.7f,1.3f),Vector3.one*.001f};
                map.transform.localScale=scales[pose];
                matrices.Add(new JObject { ["calibration"]=profile,["pose"]=pose,["matrix_column_major"]=JArray.Parse(MatrixText(map.transform)) });
                foreach (string shape in new[]{"plane","slope","two_levels","miss","inactive","no_collider"})
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Native height batch",profile+" / "+pose+" / "+shape,pose/6f))
                        throw new OperationCanceledException("Native batch cancelled; keep incomplete capture.");
                    GameObject surface = Child(map,"reference "+shape); Mesh mesh = new Mesh(); mesh.hideFlags=HideFlags.HideAndDontSave;
                    try
                    {
                        var vertices=new List<Vector3>();var triangles=new List<int>();
                        foreach(float level in shape=="two_levels" ? new[]{0f,4f} : new[]{0f})
                        {
                            int start=vertices.Count;
                            foreach(Vector2 xy in new[]{new Vector2(-20,-20),new Vector2(20,-20),new Vector2(20,20),new Vector2(-20,20)})
                                vertices.Add(inverse.MultiplyVector(new Vector3(xy.x,xy.y,level+(shape=="slope" ? .2f*xy.x+.1f*xy.y : 0))));
                            // Both sides make reflection behavior explicit rather than dependent on global backface settings.
                            foreach(int index in new[]{0,1,2,0,2,3,2,1,0,3,2,0}) triangles.Add(start+index);
                        }
                        mesh.vertices=vertices.ToArray();mesh.triangles=triangles.ToArray();mesh.RecalculateNormals();mesh.RecalculateBounds();
                        MeshCollider collider=shape=="no_collider" ? null : surface.AddComponent<MeshCollider>();
                        if(collider!=null) collider.sharedMesh=mesh;
                        Set(fixture,"surfaceSnapTargetObject",surface); Set(fixture,"surfaceQueryCollider",null);
                        if(shape=="inactive") surface.SetActive(false);
                        foreach(bool enabled in new[]{true,false})
                        foreach(Vector2 xy in new[]{new Vector2(0,0),new Vector2(-10,-10),new Vector2(10,-10),new Vector2(-10,10),new Vector2(10,10)})
                        foreach(float height in new[]{-2f,0f,2f})
                        {
                            if(collider!=null) collider.enabled=enabled;
                            float east=shape=="miss" ? 1000+xy.x : xy.x;
                            float floor=shape=="slope" ? .2f*east+.1f*xy.y : 0;
                            marker.transform.localPosition=inverse.MultiplyVector(new Vector3(east,xy.y,floor+height));
                            data.hasHeightAboveSurface=false; data.heightAboveSurface=0; data.elevationUnresolved=false;
                            JsonSpawner.Vector3Double geo=fixture.ColmapToWgs84(marker.transform.localPosition);
                            data.altitude=geo.alt;data.longitude=geo.lon;data.latitude=geo.lat;
                            fixture.RefreshLiveHeightAboveSurface(data,marker.transform);
                            bool expectedHit=shape!="miss"&&shape!="inactive"&&shape!="no_collider";
                            bool restored=collider==null || collider.enabled==enabled;
                            bool status=expectedHit ? data.hasHeightAboveSurface && data.heightSource=="mesh" &&
                                data.heightStatus==(data.heightAboveSurface<0?"below_surface":"ok") :
                                !data.hasHeightAboveSurface && data.heightSource=="unavailable" && data.heightStatus=="no_surface";
                            string scope=pose==5 ? "stress" : shape=="two_levels" ? "intended_floor_diagnostic" : "normal";
                            // Lower floor (u=0) is the declared intended surface in the two-level fixture.
                            double actual=data.hasHeightAboveSurface ? data.heightAboveSurface : double.NaN;
                            csv.WriteLine(string.Join(",",new[]{profile,pose.ToString(CI),scope,shape,enabled?"1":"0",F(east),F(xy.y),
                                expectedHit?"1":"0",F(height),data.hasHeightAboveSurface?"1":"0",data.hasHeightAboveSurface?F(actual):"",
                                data.heightSource,data.heightStatus,restored?"1":"0",status?"1":"0",data.hasHeightAboveSurface?F(actual-height):""}));
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(surface);UnityEngine.Object.DestroyImmediate(mesh); }
                }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static JArray Polygons()
    {
        var output = new JArray();
        Action<string,JArray,bool,bool,bool> run = (id,ring,exterior,expected,diagnostic) =>
        {
            JArray original=(JArray)ring.DeepClone();
            JArray heights=new JArray(Enumerable.Range(0,ring.Count).Select(i=>(object)(100+i)));
            JArray sources=new JArray(Enumerable.Range(0,ring.Count).Select(i=>(object)("v"+i)));
            JArray statuses=(JArray)sources.DeepClone();
            string error;bool accepted=GeoJsonPolygonValidator.TryNormalizeRing(ring,exterior,heights,sources,statuses,out error);
            bool closed=!accepted || JToken.DeepEquals(ring[0],ring[ring.Count-1]);
            bool metadata=true;
            if(accepted) for(int i=0;i<ring.Count;i++)
            {
                int sourceIndex=-1;for(int j=0;j<original.Count;j++) if(JToken.DeepEquals(ring[i],original[j])) {sourceIndex=j;break;}
                metadata &= sourceIndex>=0 && heights[i].Value<int>()==100+sourceIndex && sources[i].Value<string>()=="v"+sourceIndex && statuses[i].Value<string>()=="v"+sourceIndex;
            }
            if(!accepted) for(int i=0;i<original.Count;i++)
                metadata &= heights[i].Value<int>()==100+i && sources[i].Value<string>()=="v"+i && statuses[i].Value<string>()=="v"+i;
            double area=0; if(accepted) for(int i=0;i<ring.Count-1;i++) area+=ring[i][0].Value<double>()*ring[i+1][1].Value<double>()-ring[i+1][0].Value<double>()*ring[i][1].Value<double>();
            bool winding=!accepted || (exterior ? area>0 : area<0);
            bool unchanged=accepted || JToken.DeepEquals(original,ring);
            output.Add(new JObject { ["id"]=id,["scope"]=diagnostic?"diagnostic":"normal",["input"]=original,["exterior"]=exterior,
                ["expected_accepted"]=expected,["accepted"]=accepted,["output"]=ring,["error"]=error,
                ["heights"]=heights,["sources"]=sources,["statuses"]=statuses,
                ["closure_correct"]=closed,["winding_correct"]=winding,["metadata_correct"]=metadata,["rejected_input_unchanged"]=unchanged,
                ["passed"]=accepted==expected&&closed&&winding&&metadata&&unchanged });
        };
        run("ccw_exterior",Ring(0,0,1,0,1,1,0,1),true,true,false);
        run("cw_exterior",Ring(0,0,0,1,1,1,1,0),true,true,false);
        run("hole_clockwise",Ring(0,0,1,0,1,1,0,1),false,true,false);
        run("closed_ring",Ring(0,0,1,0,1,1,0,1,0,0),true,true,false);
        run("crossing",Ring(0,0,1,1,0,1,1,0),true,false,false);
        run("zero_area",Ring(0,0,1,0,2,0),true,false,false);
        run("repeated",Ring(0,0,1,0,1,0,0,1),true,false,false);
        run("overlap",Ring(0,0,2,0,1,0,1,1,0,1),true,false,false);
        run("too_few",Ring(0,0,1,0),true,false,false);
        JArray invalid=Ring(0,0,1,0,0,1);invalid[1][2]=double.NaN;run("nonfinite_altitude",invalid,true,false,false);
        invalid=Ring(0,0,1,0,0,1);invalid[1][0]=181d;run("longitude_range",invalid,true,false,false);
        return output;
    }

    private static JArray Ring(params double[] xy)
    {
        var ring=new JArray();for(int i=0;i<xy.Length;i+=2) ring.Add(new JArray(23.7+xy[i]*.001,38+xy[i+1]*.001,100d));return ring;
    }
    internal static GameObject Child(GameObject parent,string name)
    { var go=new GameObject(name);go.hideFlags=HideFlags.HideAndDontSave;go.transform.SetParent(parent.transform,false);return go; }
    internal static object Get(object obj,string name)
    { FieldInfo f=obj.GetType().GetField(name,Private);if(f==null)throw new MissingFieldException(obj.GetType().Name,name);return f.GetValue(obj); }
    internal static void Set(object obj,string name,object value)
    { FieldInfo f=obj.GetType().GetField(name,Private);if(f==null)throw new MissingFieldException(obj.GetType().Name,name);f.SetValue(obj,value); }
    internal static string F(double value) { return value.ToString("R",CI); }
    internal static string MatrixText(Transform transform)
    { var values=new JArray();Matrix4x4 m=transform.localToWorldMatrix;for(int i=0;i<16;i++)values.Add(m[i]);return values.ToString(Formatting.None); }
    internal static string NewCapture(string output,string kind)
    { string path=Path.Combine(output,kind+"_"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ",CI)+"_"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(path);return path; }
    internal static JObject Metadata(string kind,JsonSpawner source)
    {
        var hashes=new JObject();
        foreach(string directory in new[]{"TRIFFID/Runtime","TRIFFID/Editor/Experiments","TRIFFID/Editor/Checks"})
        foreach(string path in Directory.GetFiles(Path.Combine(Application.dataPath,directory),"*.cs",SearchOption.AllDirectories))
        { string relative=path.Substring(Application.dataPath.Length+1).Replace('\\','/');hashes[relative]=TextHash(File.ReadAllText(path)); }
        return new JObject { ["schema_version"]=1,["kind"]=kind,["completed"]=false,["started_utc"]=DateTime.UtcNow.ToString("O",CI),
            ["unity_version"]=Application.unityVersion,["runtime"]="Unity Editor Play mode",["source_scene"]=source.gameObject.scene.path,
            ["source_hashes_lf"]=hashes,["source_state_restored"]=false };
    }
    internal static string Hash(string path)
    { using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant(); }
    internal static string TextHash(string text)
    { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n","\n").TrimEnd('\r','\n')+"\n"))).Replace("-","").ToLowerInvariant(); }
    internal static void Write(string capture,string name,JToken value)
    { File.WriteAllText(Path.Combine(capture,name),value.ToString(Formatting.Indented),new UTF8Encoding(false)); }
    internal static void Finish(string capture,JObject metadata)
    {
        metadata["finished_utc"]=DateTime.UtcNow.ToString("O",CI);
        var hashes=new JObject();foreach(string file in Directory.GetFiles(capture,"*",SearchOption.AllDirectories))if(Path.GetFileName(file)!="app_numerics.json")hashes[file.Substring(capture.Length+1).Replace('\\','/')]=Hash(file);
        metadata["files_sha256"]=hashes;Write(capture,"app_numerics.json",metadata);
    }
}
