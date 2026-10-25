// Test doubles for scheduling, disk ownership, and transport. The coordinator,
// outbox, polygon validator, and atomic writer are the actual production files.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace UnityEngine
{
    public class GameObject { }
    public class MonoBehaviour
    {
        public bool isActiveAndEnabled=true;
        public T GetComponent<T>() { return default(T); }
        public static T[] FindObjectsByType<T>(FindObjectsSortMode sort) { return new T[0]; }
        public object StartCoroutine(IEnumerator routine) { return null; }
        public void StopAllCoroutines() { }
    }
    public enum FindObjectsSortMode { None }
    public sealed class DisallowMultipleComponent : Attribute { }
    public sealed class RequireComponent : Attribute { public RequireComponent(Type t) { } }
    public sealed class Header : Attribute { public Header(string s) { } }
    public sealed class Tooltip : Attribute { public Tooltip(string s) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class Min : Attribute { public Min(float n) { } }
    public sealed class ContextMenu : Attribute { public ContextMenu(string s) { } }
    public static class Mathf { public static float Max(float a,float b) { return Math.Max(a,b); } }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Debug { public static void Log(object s) { } public static void LogWarning(object s) { } }
    public sealed class WaitForSecondsRealtime { public WaitForSecondsRealtime(float seconds) { } }
}

public sealed class FloatingIcon { public bool IsBeingManipulated; }

public sealed class JsonSpawner
{
    public string path;
    public bool HasPendingLocalPersistence;
    public bool HasActiveAnnotationDraft;
    public int LocalPersistenceVersion;
    public bool allowCommit=true;
    public int respawns;
    public string GetResolvedSaveFilePath() { return path; }
    public bool TryWriteExternalJsonSnapshotAtomically(string json,int version)
    {
        if(!allowCommit || HasPendingLocalPersistence || version!=LocalPersistenceVersion) return false;
        GeoJsonAtomicFile.Write(path,json); return true;
    }
    public void SpawnFromJson() { respawns++; }
    public bool SpawnLatestMqttIcon(MqttLatestResponse response) { return true; }
}

public sealed class GeoJsonApiManager
{
    public sealed class ObserverSyncStatus { public int fe_updated=1; public int mobile_updated,ar_updated; }
    public List<Feature> server=new List<Feature>();
    public bool offline,holdWrites,holdFetch,loseNextCreateReply,malformedFetch;
    public ObserverSyncStatus status=new ObserverSyncStatus();
    public int createCount,updateCount,deleteCount,fetchCount;
    public bool failUpdates;
    public int localNotifications,remoteAcknowledgements;
    private Action heldWrite;
    private Action heldFetch;
    public bool HasHeldWrite { get { return heldWrite!=null; } }
    public bool HasHeldFetch { get { return heldFetch!=null; } }
    public void FetchAllFeatures(Action<bool,string,Root> callback)
    {
        fetchCount++;
        Root snapshot=new Root { features=malformedFetch?null:server.Select(GeoJsonOutbox.Clone).ToList() };
        Action deliver=()=>callback(!offline,offline?"Offline":"OK",offline?null:snapshot);
        if(holdFetch) heldFetch=deliver; else deliver();
    }
    public void ReleaseFetch() { Action action=heldFetch; heldFetch=null; action(); }
    public void ReleaseWrite() { Action action=heldWrite; heldWrite=null; action(); }
    public void CreateFeature(Feature feature,Action<bool,string,Newtonsoft.Json.Linq.JObject> callback,string operationId=null)
    {
        Action action=()=>
        {
            createCount++;
            Feature created=GeoJsonOutbox.Clone(feature); created.id="server_"+createCount; server.Add(created);
            if(loseNextCreateReply) { loseNextCreateReply=false; return; }
            callback(true,created.id,null);
        };
        if(holdWrites) heldWrite=action; else action();
    }
    public void UpdateFeature(string id,Feature feature,Action<bool,string> callback,string operationId=null)
    {
        Action action=()=>
        {
            updateCount++;
            if(failUpdates) { callback(false,"Rejected test update"); return; }
            server.RemoveAll(f=>f.id==id); server.Add(GeoJsonOutbox.Clone(feature)); callback(true,"OK");
        };
        if(holdWrites) heldWrite=action; else action();
    }
    public void DeleteFeature(string id,Action<bool,string> callback,string operationId=null)
    { deleteCount++; server.RemoveAll(f=>f.id==id); callback(true,"OK"); }
    public void AbortOperation(string operationId) { }
    public void FetchLatestMqttStatus(Action<bool,string,MqttLatestResponse> callback) { callback(false,"No telemetry fixture",null); }
    public void PatchCurrentStatusFlag(Action<bool,string,object> callback) { callback(true,"OK",null); }
    public void PatchCurrentStatusField(string field,int value,Action<bool,string,ObserverSyncStatus> callback)
    {
        remoteAcknowledgements++;
        if(field=="ar_updated") status.ar_updated=value;
        if(field=="fe_updated") status.fe_updated=value;
        if(field=="mobile_updated") status.mobile_updated=value;
        callback(true,"OK",status);
    }
    public void PatchCurrentStatusFlags(int fe,int mobile,int ar,Action<bool,string,ObserverSyncStatus> callback)
    {
        localNotifications++; status.fe_updated=fe; status.mobile_updated=mobile; status.ar_updated=ar;
        callback(true,"OK",status);
    }
    public void GetCurrentStatus(Action<bool,string,ObserverSyncStatus> callback) { callback(!offline,offline?"Offline":"OK",offline?null:status); }
}
