using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

// Observes real Play-mode frames. Editor results are never labeled headset results.
[InitializeOnLoad]
public static class AppPerformanceCollector
{
    private const double Warmup=3, Measure=15;
    private static JsonSpawner source, fixture;
    private static GameObject overlay, host;
    private static string capture, sourceData, sourceFile, sourceMatrix;
    private static JObject metadata;
    private static StreamWriter frames, events;
    private static Transform map;
    private static TransformConfig config;
    private static Feature template;
    private static GameObject prefab;
    private static string className;
    private static Vector3 centre;
    private static int phase, lastFrame, phaseFrame, saveCount;
    private static double start, nextSave;
    private static readonly int[] Counts={0,10,10,100,100,1000,1000};
    private static readonly string[] Conditions={"idle","idle","save","idle","save","idle","save"};
    private static readonly HashSet<Material> originalMaterials=new HashSet<Material>();
    public const string FrameHeader="phase,extra_features,condition,frame,warmup,phase_seconds,frame_ms,allocated_bytes,mono_used_bytes,camera_x,camera_y,camera_z,camera_qx,camera_qy,camera_qz,camera_qw,save_in_progress";

    static AppPerformanceCollector()
    {
        AssemblyReloadEvents.beforeAssemblyReload += () => Stop(false,"Script reload interrupted capture");
        EditorApplication.playModeStateChanged += state => {if(state==PlayModeStateChange.ExitingPlayMode)Stop(false,"Play mode stopped");};
    }

    [MenuItem("Tools/Experiments/App Numerics/Run Editor Frame and Save Scaling")]
    private static void StartMenu()
    {
        try
        {
            if(source!=null)throw new InvalidOperationException("A performance capture is already running.");
            if(!EditorApplication.isPlaying || EditorApplication.isPaused)throw new InvalidOperationException("Keep Play mode RUNNING and UNPAUSED for frame capture.");
            JsonSpawner selected=Selection.activeGameObject==null?null:Selection.activeGameObject.GetComponent<JsonSpawner>();
            if(selected==null)throw new InvalidOperationException("Select CoordinatesLogic / JsonSpawner.");
            GeoJsonSyncCoordinator sync=selected.GetComponent<GeoJsonSyncCoordinator>();
            if(sync!=null && sync.isActiveAndEnabled)throw new InvalidOperationException("Disable this JsonSpawner's synchronization coordinator for the isolated rendering/save benchmark. Restore it afterwards.");
            if(selected.HasActiveAnnotationDraft || (bool)AppNumericsCollector.Get(selected,"saveInProgress") ||
                (bool)AppNumericsCollector.Get(selected,"saveQueued") ||
                (int)AppNumericsCollector.Get(selected,"dataVersion")!=(int)AppNumericsCollector.Get(selected,"lastSavedVersion"))
                throw new InvalidOperationException("Finish/cancel drawing and let local saves settle before capture.");
            Root data=AppNumericsCollector.Get(selected,"currentRootData") as Root;
            if(data==null || data.features==null || data.syncState==null || data.syncState.pending.Count!=0)
                throw new InvalidOperationException("Load the scene and settle pending edits first.");
            Camera camera=Camera.main;if(camera==null)throw new InvalidOperationException("The running scene needs a main camera.");
            Transform selectedMap=selected.mapTransform!=null?selected.mapTransform:selected.transform;
            PointData visible=selectedMap.GetComponentsInChildren<PointData>(true).FirstOrDefault(p=>
            {
                if(!p.gameObject.activeInHierarchy || p.GetComponentsInChildren<Renderer>(true).Length==0)return false;
                Vector3 v=camera.WorldToViewportPoint(p.transform.position);return v.z>0&&v.x>.1f&&v.x<.9f&&v.y>.1f&&v.y<.9f;
            });
            if(visible==null)throw new InvalidOperationException("First aim the Game camera at an existing POI near the centre of the view.");
            PrefabEntry entry=selected.prefabEntries.FirstOrDefault(e=>e!=null&&e.prefab!=null&&string.Equals(e.name,"fire",StringComparison.OrdinalIgnoreCase)) ??
                selected.prefabEntries.FirstOrDefault(e=>e!=null&&e.prefab!=null);
            if(entry==null)throw new InvalidOperationException("No configured point prefab is available.");
            Feature point=data.features.FirstOrDefault(f=>f.geometry!=null&&f.geometry.type=="Point");
            if(point==null)throw new InvalidOperationException("No point feature is loaded.");
            string folder=EditorUtility.OpenFolderPanel("Choose a parent folder for the frame capture","","");if(string.IsNullOrEmpty(folder))return;
            capture=null;metadata=null;frames=null;events=null;
            source=selected;map=selectedMap;prefab=entry.prefab;className=entry.name;template=GeoJsonOutbox.Clone(point);
            config=JsonConvert.DeserializeObject<TransformConfig>(JsonConvert.SerializeObject(AppNumericsCollector.Get(source,"transformData")));
            centre=GeoUtils.GetColmapPosition(visible.longitude,visible.latitude,visible.altitude,config);
            sourceData=JsonConvert.SerializeObject(data);sourceFile=File.Exists(source.saveFilePath)?AppNumericsCollector.Hash(source.saveFilePath):null;
            sourceMatrix=AppNumericsCollector.MatrixText(map);
            capture=AppNumericsCollector.NewCapture(folder,"AppPerformance");metadata=AppNumericsCollector.Metadata("editor_frame_save_scaling",source);
            metadata["baseline_features"]=data.features.Count;metadata["extra_feature_counts"]=new JArray(Counts);
            metadata["conditions"]=new JArray(Conditions);metadata["warmup_seconds"]=Warmup;metadata["measurement_seconds_per_phase"]=Measure;
            metadata["geometry_mix"]="80% points, 10% three-vertex lines, 10% four-corner polygons (closed five-tuple rings)";
            metadata["scope"]="Real Unity Editor Play frames with additional annotations spawned through production JsonSpawner, and forced unedited asynchronous saves of the disconnected fixture. Original reconstruction stays visible. No networking or target-device performance claim.";
            metadata["limits"]="Profiler/CSV capture overhead is included. Fixed camera/view is requested and recorded; source scene content is additional baseline workload. No GPU-only timing, interaction latency or standalone headset capture.";
            metadata["hardware"]=new JObject { ["cpu"]=SystemInfo.processorType,["gpu"]=SystemInfo.graphicsDeviceName,["ram_mb"]=SystemInfo.systemMemorySize,
                ["screen_width"]=Screen.width,["screen_height"]=Screen.height,["vsync_count"]=QualitySettings.vSyncCount,["target_frame_rate"]=Application.targetFrameRate };
            metadata["point_prefab"]=prefab.name;metadata["fixture_class"]=className;metadata["fixture_centre_colmap"]=new JArray(centre.x,centre.y,centre.z);
            metadata["source_save_sha256_before"]=sourceFile;
            AppNumericsCollector.Write(capture,"alignment.json",JObject.FromObject(config));
            foreach(Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))foreach(Material material in renderer.sharedMaterials)if(material!=null)originalMaterials.Add(material);
            if(source.nodePrefab!=null)foreach(Renderer renderer in source.nodePrefab.GetComponentsInChildren<Renderer>(true))
                foreach(Material material in renderer.sharedMaterials)if(material!=null)originalMaterials.Add(material);
            frames=new StreamWriter(Path.Combine(capture,"frames.csv"));frames.WriteLine(FrameHeader);
            events=new StreamWriter(Path.Combine(capture,"events.jsonl"));phase=0;lastFrame=-1;BeginPhase();
            EditorApplication.update+=Tick;
            Debug.Log("[AppPerformance] Capturing seven annotation phases, approximately 2 minutes. Keep the camera still, Game view visible and Play UNPAUSED; do not edit or grab anything. Capture: "+capture);
        }
        catch(Exception error){Stop(false,error.ToString());Debug.LogException(error);}
    }

    [MenuItem("Tools/Experiments/App Numerics/Cancel Frame Capture")]
    private static void Cancel() { Stop(false,"Cancelled by user"); }

    private static void BeginPhase()
    {
        DisposeFixture();phaseFrame=0;saveCount=0;
        int count=Counts[phase];
        if(count>0)
        {
            string directory=Path.Combine(capture,"phase_"+phase);Directory.CreateDirectory(directory);
            var features=new List<Feature>();float[] r=config.colmap_to_enu.R_rowmajor;float s=config.colmap_to_enu.scale;
            for(int i=0;i<count;i++)
            {
                // Same 4 m footprint and distribution for each workload.
                float e=((i*37%101)/100f-.5f)*4f,n=((i*61%103)/102f-.5f)*4f;
                Vector3 local=centre+new Vector3(r[0]*e+r[3]*n,r[1]*e+r[4]*n,r[2]*e+r[5]*n)/s;
                double lon,lat,alt;GeoUtils.ColmapToWgs84(local,config,out lon,out lat,out alt);
                Feature feature=GeoJsonOutbox.Clone(template);feature.id="perf_"+i.ToString("D4");
                feature.properties.id=feature.id;feature.properties.className=className;
                feature.geometry.coordinates=new JArray(lon,lat,alt);
                if(i%10==8) feature.geometry=new Geometry {type="LineString",coordinates=new JArray(Coordinate(e-.5f,n),Coordinate(e,n+.5f),Coordinate(e+.5f,n))};
                if(i%10==9)
                {
                    JArray first=Coordinate(e-.5f,n-.5f);
                    var ring=new JArray(first,Coordinate(e+.5f,n-.5f),Coordinate(e+.5f,n+.5f),Coordinate(e-.5f,n+.5f),first.DeepClone());
                    var polygon=new JArray();polygon.Add(ring);feature.geometry=new Geometry {type="Polygon",coordinates=polygon};
                }
                features.Add(feature);
            }
            string input=Path.Combine(directory,"fixture.geojson");
            File.WriteAllText(input,JsonConvert.SerializeObject(new Root {features=features},Formatting.Indented));
            overlay=new GameObject("AppPerformance owned annotation workload");overlay.hideFlags=HideFlags.HideAndDontSave;overlay.transform.SetParent(map,false);
            host=AppNumericsCollector.Child(overlay,"inactive benchmark spawner");host.SetActive(false);
            fixture=host.AddComponent<JsonSpawner>();fixture.mapTransform=overlay.transform;fixture.transformJsonPath=Path.Combine(capture,"alignment.json");
            fixture.geoJsonPath=input;fixture.saveFilePath=Path.Combine(directory,"state.json");fixture.nodePrefab=source.nodePrefab;
            fixture.prefabEntries=new List<PrefabEntry>{new PrefabEntry{name=className,prefab=prefab}};
            AppNumericsCollector.Set(fixture,"useSharedTransformPathJson",false);AppNumericsCollector.Set(fixture,"enableAutoSaveTimer",false);
            AppNumericsCollector.Set(fixture,"enableSurfaceSnapFallback",false);AppNumericsCollector.Set(fixture,"autoActivateSurfaceForSnap",false);
            AppNumericsCollector.Set(fixture,"surfaceSnapTargetObject",AppNumericsCollector.Child(overlay,"no surface collider"));
            SceneConsistencySession.Invoke(fixture,"Awake");fixture.SpawnFromJson();
            Root loaded=AppNumericsCollector.Get(fixture,"currentRootData") as Root;
            if(loaded==null || loaded.features.Count!=count || overlay.GetComponentsInChildren<PointData>(true).Select(x=>x.pointID).Distinct().Count()!=count)
                throw new InvalidDataException("Not all benchmark features spawned; keep this failed capture.");
            if(!overlay.GetComponentsInChildren<Renderer>(true).Any(x=>x.enabled&&x.gameObject.activeInHierarchy))
                throw new InvalidDataException("The benchmark produced no active renderers. Check point/node prefabs before rerunning.");
        }
        start=EditorApplication.timeSinceStartup;nextSave=Warmup+1;
        Event("phase_start",new JObject { ["extra_features"]=count,["condition"]=Conditions[phase],
            ["phase_clock_start_seconds"]=start,
            ["fixture_renderers"]=overlay==null?0:overlay.GetComponentsInChildren<Renderer>(true).Count(x=>x.enabled&&x.gameObject.activeInHierarchy) });
    }

    private static JArray Coordinate(float e,float n)
    {
        float[] r=config.colmap_to_enu.R_rowmajor;float s=config.colmap_to_enu.scale;
        Vector3 local=centre+new Vector3(r[0]*e+r[3]*n,r[1]*e+r[4]*n,r[2]*e+r[5]*n)/s;
        double lon,lat,alt;GeoUtils.ColmapToWgs84(local,config,out lon,out lat,out alt);return new JArray(lon,lat,alt);
    }

    private static void Tick()
    {
        if(source==null)return;
        try
        {
            if(!EditorApplication.isPlaying || EditorApplication.isPaused)throw new InvalidOperationException("Frame capture interrupted by stopped/paused Play mode.");
            if(lastFrame==Time.frameCount)return;lastFrame=Time.frameCount;phaseFrame++;
            Camera camera=Camera.main;if(camera==null)throw new InvalidOperationException("Main camera disappeared.");
            double elapsed=EditorApplication.timeSinceStartup-start;
            bool saving=fixture!=null&&(bool)AppNumericsCollector.Get(fixture,"saveInProgress");
            Vector3 p=camera.transform.position;Quaternion q=camera.transform.rotation;
            frames.WriteLine(string.Join(",",new[]{phase.ToString(),Counts[phase].ToString(),Conditions[phase],Time.frameCount.ToString(),elapsed<Warmup?"1":"0",
                AppNumericsCollector.F(elapsed),AppNumericsCollector.F(Time.unscaledDeltaTime*1000d),Profiler.GetTotalAllocatedMemoryLong().ToString(),Profiler.GetMonoUsedSizeLong().ToString(),
                AppNumericsCollector.F(p.x),AppNumericsCollector.F(p.y),AppNumericsCollector.F(p.z),AppNumericsCollector.F(q.x),AppNumericsCollector.F(q.y),AppNumericsCollector.F(q.z),AppNumericsCollector.F(q.w),saving?"1":"0"}));
            if(elapsed>=Warmup+Measure)
            {
                if(fixture!=null&&((bool)AppNumericsCollector.Get(fixture,"saveInProgress")||(bool)AppNumericsCollector.Get(fixture,"saveQueued")||
                    (int)AppNumericsCollector.Get(fixture,"dataVersion")!=(int)AppNumericsCollector.Get(fixture,"lastSavedVersion")))
                {if(elapsed>Warmup+Measure+30)throw new TimeoutException("Benchmark saves did not settle.");return;}
                Event("phase_end",new JObject { ["observed_frames"]=phaseFrame,["save_requests"]=saveCount });
                phase++;if(phase==Counts.Length){Stop(true,null);return;}BeginPhase();return;
            }
            if(fixture!=null&&Conditions[phase]=="save"&&elapsed>=nextSave)
            {
                double before=EditorApplication.timeSinceStartup;fixture.SaveToJson();saveCount++;nextSave+=1;
                Event("save_requested",new JObject { ["main_thread_call_ms"]=(EditorApplication.timeSinceStartup-before)*1000,
                    ["outbox_operations"]=((Root)AppNumericsCollector.Get(fixture,"currentRootData")).syncState.pending.Count });
            }
        }
        catch(Exception error){Stop(false,error.ToString());Debug.LogException(error);}
    }

    private static void Event(string kind,JObject extra)
    { extra["kind"]=kind;extra["phase"]=phase;extra["editor_seconds"]=EditorApplication.timeSinceStartup;events.WriteLine(extra.ToString(Formatting.None)); }

    private static void DisposeFixture()
    {
        if(overlay!=null)
        {
            var owned=new HashSet<Material>();foreach(Renderer renderer in overlay.GetComponentsInChildren<Renderer>(true))
                foreach(Material material in renderer.sharedMaterials)if(material!=null&&!originalMaterials.Contains(material)&&!EditorUtility.IsPersistent(material))owned.Add(material);
            UnityEngine.Object.DestroyImmediate(overlay);foreach(Material material in owned)if(material!=null)UnityEngine.Object.DestroyImmediate(material);
        }
        overlay=null;host=null;fixture=null;
    }

    private static void Stop(bool completed,string error)
    {
        EditorApplication.update-=Tick;
        if(ReferenceEquals(source,null))return;
        try
        {
            frames?.Dispose();events?.Dispose();DisposeFixture();
            if(metadata==null || capture==null)return;
            metadata["completed"]=completed;metadata["error"]=error;
            metadata["source_state_restored"]=source!=null&&sourceData==JsonConvert.SerializeObject(AppNumericsCollector.Get(source,"currentRootData"))&&
                sourceFile==(File.Exists(source.saveFilePath)?AppNumericsCollector.Hash(source.saveFilePath):null)&&sourceMatrix==AppNumericsCollector.MatrixText(map);
            metadata["source_save_sha256_after"]=source!=null&&File.Exists(source.saveFilePath)?AppNumericsCollector.Hash(source.saveFilePath):null;
            AppNumericsCollector.Finish(capture,metadata);
            Debug.Log("[AppPerformance] "+(completed?"Completed":"INCOMPLETE")+" Editor capture: "+capture);
        }
        finally { source=null;fixture=null;frames=null;events=null;originalMaterials.Clear(); }
    }
}
