using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class CoordinatorRegressionTests
{
    private static int passed,failed;
    public static int Run()
    {
        passed=failed=0;
        Test("coordinator restores an offline create and delivers it after reconnection", () =>
        {
            Root root=Empty(); root.features.Add(Point("feature_a",1)); GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow);
            using(Fixture f=new Fixture(root))
            {
                f.api.offline=true; f.Steps(25); Check(f.api.createCount==0 && f.Load().syncState.pending.Count==1);
                f.RestartWorker(); f.api.offline=false; f.Until(()=>f.Load().syncState.pending.Count==0);
                Check(f.api.createCount==1 && f.api.server.Count==1);
            }
        });
        Test("coordinator restores and delivers an offline deletion", () =>
        {
            Feature original=Point("a",1); Root root=Clean(original); GeoJsonOutbox.RecordDeletion(root,root.features[0]); root.features.Clear();
            using(Fixture f=new Fixture(root,original))
            {
                f.api.offline=true; f.Steps(15); f.RestartWorker(); f.api.offline=false;
                f.Until(()=>f.Load().syncState.pending.Count==0); Check(f.api.deleteCount==1 && f.api.server.Count==0);
            }
        });
        Test("coordinator keeps a newer local edit when an earlier write acknowledgement arrives", () =>
        {
            Feature original=Point("a",1); Root root=Clean(original);
            using(Fixture f=new Fixture(root,original))
            {
                f.Edit("a",2); f.api.holdWrites=true; f.Until(()=>f.api.HasHeldWrite);
                f.Edit("a",3); f.api.holdWrites=false; f.api.ReleaseWrite();
                f.Until(()=>f.Load().syncState.pending.Count==0);
                Equal(X(f.api.server[0]),3); Check(f.api.updateCount==2);
            }
        });
        Test("coordinator reconciles a committed create whose reply was lost without a duplicate", () =>
        {
            Root root=Empty(); root.features.Add(Point("feature_a",1)); GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow);
            using(Fixture f=new Fixture(root))
            {
                f.api.loseNextCreateReply=true; f.Until(()=>f.api.createCount==1 && f.Load().syncState.pending.Count==0);
                Check(f.api.server.Count==1 && f.api.createCount==1);
            }
        });
        Test("coordinator receives unrelated remote features after retry exhaustion", () =>
        {
            Feature original=Point("a",1);
            using(Fixture f=new Fixture(Clean(original),original))
            {
                f.Edit("a",2); f.api.failUpdates=true; Set(f.worker,"maxRetries",0); Set(f.worker,"retryMaxDelaySeconds",3600f);
                f.Until(()=>f.Load().syncState.pending.Any(p=>p.status=="failed"));
                f.api.server.Add(Point("robot_b",7)); f.worker.RefreshFromServer();
                f.Until(()=>f.Load().features.Any(p=>p.id=="robot_b"));
                Equal(X(f.Load().features.Find(p=>p.id=="a")),2); Check(f.Load().syncState.pending.Count==1);
            }
        });
        Test("coordinator rejects a stale GET when a local save changes its version", () =>
        {
            Feature original=Point("a",1);
            using(Fixture f=new Fixture(Clean(original),original))
            {
                f.Edit("a",2); f.api.holdFetch=true; f.Until(()=>f.api.HasHeldFetch);
                f.Edit("a",3); f.api.holdFetch=false; f.api.ReleaseFetch();
                f.Until(()=>f.Load().syncState.pending.Count==0);
                Equal(X(f.api.server[0]),3); Check(f.api.updateCount==1);
            }
        });
        Test("coordinator never sends a request before its dispatch state is durably committed", () =>
        {
            Root root=Empty(); root.features.Add(Point("feature_a",1)); GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow);
            using(Fixture f=new Fixture(root))
            {
                f.spawner.allowCommit=false; f.Steps(30);
                Check(f.api.createCount==0 && !f.Load().syncState.pending[0].mayHaveReachedServer);
            }
        });
        Test("coordinator preserves conflicting legacy data and makes no blind write", () =>
        {
            Root root=new Root { features=new List<Feature> { Point("a",2) } };
            using(Fixture f=new Fixture(root,Point("a",3)))
            {
                f.worker.RefreshFromServer(); f.Until(()=>f.Load().syncState!=null && f.Load().syncState.pending.Any(p=>p.status=="conflict"));
                Equal(X(f.Load().features[0]),2); Check(f.api.updateCount==0 && f.api.createCount==0);
            }
        });
        Console.WriteLine("COORDINATOR RESULT: "+passed+" passed, "+failed+" failed");
        return failed==0?0:1;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory;
        private Stack<IEnumerator> stack;
        public JsonSpawner spawner;
        public GeoJsonApiManager api=new GeoJsonApiManager();
        public GeoJsonSyncCoordinator worker;
        public Fixture(Root root,params Feature[] remote)
        {
            directory=Path.Combine(Path.GetTempPath(),"ar2_coordinator_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            api.server=remote.Select(GeoJsonOutbox.Clone).ToList();
            spawner=new JsonSpawner { path=Path.Combine(directory,"state.json") };
            GeoJsonAtomicFile.Write(spawner.path,JsonConvert.SerializeObject(root)); RestartWorker();
        }
        public Root Load() { return JsonConvert.DeserializeObject<Root>(File.ReadAllText(spawner.path)); }
        public void Edit(string id,double value)
        {
            Root root=Load(); ((JArray)root.features.Find(f=>f.id==id).geometry.coordinates)[0]=value;
            GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow); GeoJsonAtomicFile.Write(spawner.path,JsonConvert.SerializeObject(root));
            spawner.LocalPersistenceVersion++;
        }
        public void RestartWorker()
        {
            string path=spawner.path; spawner=new JsonSpawner { path=path };
            worker=new GeoJsonSyncCoordinator(); Set(worker,"jsonSpawner",spawner); Set(worker,"apiManager",api);
            Set(worker,"verboseLogs",false); Set(worker,"restorePendingState",true); Set(worker,"updateDebounceSeconds",0f);
            Set(worker,"nextRemoteCheck",DateTime.UtcNow.AddHours(1)); Set(worker,"remotePollingIntervalSeconds",3600f);
            Set(worker,"apiCallbackTimeoutSeconds",1f);
            stack=new Stack<IEnumerator>();
            stack.Push((IEnumerator)typeof(GeoJsonSyncCoordinator).GetMethod("Synchronize",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(worker,null));
        }
        private void Step()
        {
            int budget=10000;
            while(stack.Count>0 && budget-->0)
            {
                IEnumerator current=stack.Peek();
                if(!current.MoveNext()) { stack.Pop(); continue; }
                IEnumerator nested=current.Current as IEnumerator;
                if(nested!=null) { stack.Push(nested); continue; }
                Time.realtimeSinceStartup+=0.1f; return;
            }
            throw new Exception("Coroutine failed to yield or stopped.");
        }
        public void Steps(int count) { for(int i=0;i<count;i++) Step(); }
        public void Until(Func<bool> condition)
        {
            for(int i=0;i<2000;i++) { if(condition()) return; Step(); }
            throw new Exception("Condition did not converge. "+worker.LastStatus+" State: "+JsonConvert.SerializeObject(Load().syncState));
        }
        public void Dispose() { Directory.Delete(directory,true); }
    }
    private static Root Empty() { Root r=new Root { features=new List<Feature>() }; GeoJsonOutbox.Initialize(r,false); return r; }
    private static Root Clean(Feature f) { Root r=Empty(); GeoJsonOutbox.MergeRemote(r,new Root { features=new List<Feature> { GeoJsonOutbox.Clone(f) } }); return r; }
    private static Feature Point(string id,double x) { return new Feature { id=id,properties=new Properties { id=id,className="person",source="test",altitude_m=5 },geometry=new Geometry { type="Point",coordinates=new JArray(x,38d,5d) } }; }
    private static double X(Feature f) { return ((JArray)f.geometry.coordinates)[0].Value<double>(); }
    private static void Set(object target,string field,object value) { typeof(GeoJsonSyncCoordinator).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
    private static void Check(bool value) { if(!value) throw new Exception("Assertion failed"); }
    private static void Equal(double actual,double expected) { Check(Math.Abs(actual-expected)<1e-8); }
    private static void Test(string name,Action action) { try { action(); passed++; Console.WriteLine("PASS "+name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex); } }
}
