using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Editor-only commands invoke production APIs. Never changes the outbox or acks
// network operations. Not evidence of controller use or a process crash.
[InitializeOnLoad]
public static class SyncMatrixBridge
{
    const string Key = "TRIFFID.SyncMatrix.Directory";
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string Boot = Guid.NewGuid().ToString("N");
    static SyncMatrixBridge()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            string folder=SessionState.GetString(Key, "");
            if(folder!="")File.AppendAllText(Path.Combine(folder,"matrix_lifecycle.jsonl"),
                new JObject{["state"]=state.ToString(),["utc"]=DateTime.UtcNow.ToString("O"),["boot_id"]=Boot}.ToString(Formatting.None)+"\n");
        };
    }

    [MenuItem("Tools/Experiments/Synchronization/Attach Automated Matrix")]
    static void Attach()
    {
        try
        {
            if(SessionState.GetString(Key,"")!="")throw new InvalidOperationException("A matrix bridge is already attached. Detach it before attaching a new run.");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Start Play unpaused first.");
            var source = Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponent<JsonSpawner>();
            if (source == null) throw new InvalidOperationException("Select CoordinatesLogic / JsonSpawner.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(source.saveFilePath));
            if (Path.GetFileName(source.saveFilePath) != "unity_state.json" || !File.Exists(Path.Combine(folder,"matrix_run.json")))
                throw new InvalidOperationException("Use the matrix runner's unity_state.json path, configured BEFORE Play.");
            var run = JObject.Parse(File.ReadAllText(Path.Combine(folder,"matrix_run.json")));
            if (run.Value<int>("schema_version") != 1 || run.Value<string>("kind") != "native_sync_matrix") throw new InvalidDataException("Invalid matrix run.");
            SessionState.SetString(Key, folder);
            SessionState.SetString(Key+".source", source.gameObject.name);
            SessionState.SetInt(Key+".seq", 0);
            SessionState.EraseString(Key+".pending");
            Debug.Log("[SyncMatrix] Attached. Follow Python; leave Play unpaused and do not edit. Play may restart automatically.");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    [MenuItem("Tools/Experiments/Synchronization/Detach Automated Matrix")]
    static void Detach() { SessionState.EraseString(Key); Debug.Log("[SyncMatrix] Detached."); }

    static object Get(object obj,string name)
    {
        var f=obj.GetType().GetField(name,Private);
        if(f==null)throw new MissingFieldException(obj.GetType().Name,name);
        return f.GetValue(obj);
    }
    static void Write(string path,JObject data)
    {
        string temp=path+".tmp";
        File.WriteAllText(temp,data.ToString(Formatting.Indented),new UTF8Encoding(false));
        if(File.Exists(path))File.Delete(path);
        File.Move(temp,path);
    }
    static void Tick()
    {
        string folder=SessionState.GetString(Key,"");
        if(folder=="" || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode!=EditorApplication.isPlaying)return;
        // Retry response delivery separately, never replay an executed edit if
        // Windows temporarily locks the response file while Python reads it.
        if(SessionState.GetString(Key+".pending","")!=""){FlushResponse(folder);return;}
        string commandPath=Path.Combine(folder,"matrix_command.json");
        if(!File.Exists(commandPath))return;
        JObject command;
        try {command=JObject.Parse(File.ReadAllText(commandPath));} catch(IOException){return;} catch(JsonException){return;}
        int seq=command.Value<int>("seq");
        if(seq<=SessionState.GetInt(Key+".seq",0))return;
        var response=new JObject{["seq"]=seq,["run_id"]=command["run_id"],["boot_id"]=Boot,
            ["utc"]=DateTime.UtcNow.ToString("O"),["unity_version"]=Application.unityVersion};
        string kind=command.Value<string>("kind");
        try
        {
            var run=JObject.Parse(File.ReadAllText(Path.Combine(folder,"matrix_run.json")));
            if(command.Value<string>("run_id")!=run.Value<string>("run_id"))throw new InvalidDataException("Run ID mismatch.");
            if(kind=="stop_play" || kind=="start_play")
            {
                if(EditorApplication.isPlaying!=(kind=="stop_play"))throw new InvalidOperationException("Unexpected Play state.");
                response["play_transition"]=kind;
            }
            else
            {
                if(!EditorApplication.isPlaying || EditorApplication.isPaused)throw new InvalidOperationException("Keep Play unpaused.");
                var matches=Resources.FindObjectsOfTypeAll<JsonSpawner>().Where(s=>s.gameObject.scene.IsValid() && s.isActiveAndEnabled &&
                    s.gameObject.name==SessionState.GetString(Key+".source","") && Path.GetFullPath(s.saveFilePath)==Path.Combine(folder,"unity_state.json")).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Expected exactly one active selected spawner with the matrix save path.");
                var source=matches[0];var coordinator=source.GetComponent<GeoJsonSyncCoordinator>();
                if(coordinator==null || !coordinator.isActiveAndEnabled)throw new InvalidOperationException("Enable synchronization coordinator.");
                var api=(GeoJsonApiManager)Get(coordinator,"apiManager");
                if(api==null || !(bool)Get(api,"useLocalhost"))throw new InvalidOperationException("Enable Use Localhost before Play.");
                int port=run.Value<int>("port");
                foreach(var pair in new[]{new[]{"GetFeaturesUrl","/wp-json/map-manager/v1/features"},new[]{"GetStatusUrl","/wp-json/observer-sync/v1/status"},new[]{"GetLatestMqttUrl","/wp-json/mqtt/v1/latest"}})
                    if((string)api.GetType().GetMethod(pair[0],Private).Invoke(api,null)!="http://127.0.0.1:"+port+pair[1])throw new InvalidOperationException("Matrix endpoints must be private localhost.");
                if(source.HasActiveAnnotationDraft || (bool)Get(source,"saveInProgress") || (bool)Get(source,"saveQueued"))return;
                var root=(Root)Get(source,"currentRootData");
                if(root==null || root.features==null)return;
                response["features_before"]=JArray.FromObject(root.features);
                Transform map=source.mapTransform!=null?source.mapTransform:source.transform;
                string id=command.Value<string>("feature_id");
                Func<PointData> target=()=>map.GetComponentsInChildren<PointData>(true).Single(p=>p.pointID==id);
                if(kind=="config")
                {
                    var cfg=new JObject();
                    foreach(string n in new[]{"localScanIntervalSeconds","updateDebounceSeconds","maxRetries","retryBaseDelaySeconds","retryMaxDelaySeconds","enableRemotePolling","remotePollingIntervalSeconds","localChangeGraceSeconds","apiCallbackTimeoutSeconds"})cfg[n]=JToken.FromObject(Get(coordinator,n));
                    response["coordinator"]=cfg;response["alignment"]=JToken.FromObject(Get(source,"transformData"));
                }
                else if(kind=="noop")source.SaveToJson();
                else if(kind=="refresh")coordinator.RefreshFromServer();
                else if(kind=="create")
                {
                    var anchor=target();var obj=new GameObject("Matrix production point");obj.transform.SetParent(map,false);
                    obj.transform.position=anchor.transform.position+map.TransformVector(new Vector3(.01f,0,.01f));
                    response["created_id"]=source.AddPointFeatureAndSave(obj,"fire","Ground Station");
                }
                else if(kind=="delete")
                {if(!source.DeleteFeatureByNode(target().transform))throw new InvalidOperationException("Production delete rejected.");}
                else if(kind=="move")
                {
                    var point=target();source.BeginFeatureEdit(point.transform);
                    point.transform.position+=map.TransformVector(new Vector3(.01f,0,.01f));
                    if(!source.SyncData(point,point.transform))throw new InvalidOperationException("Production edit rejected.");
                    source.SaveToJson();
                }
                else throw new InvalidDataException("Unknown command.");
                response["features_after"]=JArray.FromObject(root.features);
            }
            response["ok"]=true;
        }
        catch(Exception e){response["ok"]=false;response["error"]=e.ToString();Debug.LogException(e);}
        SessionState.SetInt(Key+".seq",seq);
        SessionState.SetString(Key+".pending",response.ToString(Formatting.None));
        FlushResponse(folder);
    }
    static void FlushResponse(string folder)
    {
        var response=JObject.Parse(SessionState.GetString(Key+".pending",""));
        try { Write(Path.Combine(folder,"matrix_response.json"),response); }
        catch(IOException){return;}
        catch(UnauthorizedAccessException){return;}
        SessionState.EraseString(Key+".pending");
        string transition=response.Value<string>("play_transition");
        if(transition!=null && response.Value<bool>("ok"))EditorApplication.isPlaying=transition=="start_play";
    }
}
