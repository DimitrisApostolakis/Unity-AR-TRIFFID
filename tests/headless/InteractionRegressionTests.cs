using System;
using System.Reflection;
using TMPro;
using UnityEngine;

public static class InteractionRegressionTests
{
    private static int passed,failed;
    public static int Main()
    {
        Test("panel shows confirmed server ID and refreshes after acknowledgement",()=>
        {
            using(var fixture=new PanelFixture())
            {
                var idText=new GameObject().AddComponent<TextMeshProUGUI>();
                Set(fixture.panel,"idValueText",idText);
                var marker=fixture.Register("feature_local");
                Check(idText.text=="-");
                marker.serverFeatureId="server_482";marker.NotifyDataChanged();
                Check(idText.text=="server_482" && marker.pointID=="feature_local");
                marker.serverFeatureId=null;marker.NotifyDataChanged();Check(idText.text=="-");
            }
        });
        Test("disabled surface is enabled only during a successful query",()=>
        {
            Collider c=Surface(false); c.query=(ray,d)=>new RaycastHit { point=new Vector3(1,2,3) };
            Vector3 hit; Check(Query(c,out hit)); Check(hit.y==2 && !c.enabled && c.stateChanges==3);
        });
        Test("disabled surface is restored after both directions miss",()=>
        {
            Collider c=Surface(false); int calls=0; c.query=(ray,d)=>{calls++;return null;};
            Vector3 hit; Check(!Query(c,out hit) && calls==2 && !c.enabled);
        });
        Test("disabled surface is restored if raycasting throws",()=>
        {
            Collider c=Surface(false); c.query=(ray,d)=>{throw new InvalidOperationException("test");}; bool threw=false;
            Vector3 hit; try { Query(c,out hit); } catch(InvalidOperationException) { threw=true; }
            Check(threw && !c.enabled);
        });
        Test("an enabled surface keeps its interaction state",()=>
        {
            Collider c=Surface(true); c.query=(ray,d)=>new RaycastHit(); Vector3 hit;
            Check(Query(c,out hit) && c.enabled && c.stateChanges==0);
        });
        Test("a reverse-direction hit also restores a disabled surface",()=>
        {
            Collider c=Surface(false); c.query=(ray,d)=>ray.direction.y>0?(RaycastHit?)new RaycastHit():null;
            Vector3 hit; Check(Query(c,out hit) && !c.enabled);
        });
        Test("invalid rays and inactive objects do not change collider state",()=>
        {
            Collider c=Surface(false); Vector3 hit;
            Check(!GeographicSurfaceQuery.TryHit(c,Vector3.zero,Vector3.up,1,-1,~0,out hit));
            c.gameObject.SetActive(false); Check(!Query(c,out hit)); Check(!c.enabled && c.stateChanges==1 && !c.gameObject.activeSelf);
        });
        Test("global queries still exclude marker colliders",()=>
        {
            Collider marker=Surface(true); marker.gameObject.AddComponent<PointData>(); Collider surface=Surface(true);
            Physics.hits=new[] { new RaycastHit { collider=surface,distance=2,point=new Vector3(0,5,0) }, new RaycastHit { collider=marker,distance=1,point=new Vector3(0,9,0) } };
            Vector3 hit; Check(Query(null,out hit) && hit.y==5);
        });
        Test("ray hover selects its marker while unrelated metadata updates cannot change it",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                PointData a=f.Register("a"), b=f.Register("b"); f.panel.OnMarkerSelected(b);
                MarkerEventManager.RaiseMarkerHovered(a); Check(f.panel.IsObservingMarker(a));
                b.NotifyDataChanged(); f.panel.LiveUpdateIfCurrent(b); Check(f.panel.IsObservingMarker(a));
                MarkerEventManager.RaiseMarkerSelected(b); Check(f.panel.IsObservingMarker(b));
            }
        });
        Test("next and previous remain explicit selection actions",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                PointData a=f.Register("a"), b=f.Register("b"); f.panel.ShowNext(); Check(f.panel.IsObservingMarker(b));
                f.panel.ShowPrevious(); Check(f.panel.IsObservingMarker(a));
            }
        });
        Test("server respawning preserves the selected feature despite a different order",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                f.Register("a"); PointData b=f.Register("b"); f.panel.OnMarkerSelected(b);
                f.panel.BeginMarkerRefresh(); f.panel.ClearMarkers(); PointData newB=f.Register("b"); f.Register("a"); f.panel.EndMarkerRefresh();
                Check(f.panel.IsObservingMarker(newB));
            }
        });
        Test("server respawning preserves the selected annotation vertex",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                f.Register("line"); PointData vertex=f.Register("line"); f.panel.OnMarkerSelected(vertex);
                f.panel.BeginMarkerRefresh(); f.panel.ClearMarkers(); f.Register("other"); f.Register("line"); PointData newVertex=f.Register("line"); f.panel.EndMarkerRefresh();
                Check(f.panel.IsObservingMarker(newVertex));
            }
        });
        Test("deleting a different marker does not shift the selection",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                PointData a=f.Register("a"), b=f.Register("b"); f.Register("c"); f.panel.OnMarkerSelected(b);
                f.panel.UnregisterMarker(a); Check(f.panel.IsObservingMarker(b));
            }
        });
        Test("a selected feature deleted on the server leaves an empty panel until selection",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                f.Register("a"); PointData b=f.Register("b"); f.panel.OnMarkerSelected(b);
                f.panel.BeginMarkerRefresh(); f.panel.ClearMarkers(); PointData a=f.Register("a"); f.panel.EndMarkerRefresh();
                Check(!f.panel.IsObservingMarker(a) && f.height.text=="-"); f.Register("c"); Check(!f.panel.IsObservingMarker(a));
                f.panel.ShowNext(); Check(f.panel.IsObservingMarker(a));
            }
        });
        Test("closing and reopening the panel keeps the selected feature",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                f.Register("a"); PointData b=f.Register("b"); f.panel.OnMarkerSelected(b);
                Invoke(f.panel,"OnDisable"); Invoke(f.panel,"OnEnable"); Check(f.panel.IsObservingMarker(b));
            }
        });
        Test("source stays visible without sync status and estimated height uses a second line",()=>
        {
            using(PanelFixture f=new PanelFixture())
            {
                PointData a=f.Register("a"); a.syncStatus="Pending"; a.source="GROUND STATION";
                a.hasHeightAboveSurface=true; a.heightAboveSurface=6.72; a.heightSource="retained"; a.NotifyDataChanged();
                Check(f.source.text=="GROUND STATION"); Check(f.height.text=="6.72 m\n<size=70%>(Estimated)</size>");
                a.heightSource="mesh"; a.heightAboveSurface=-0.4; a.NotifyDataChanged(); Check(f.height.text=="-0.40 m");
                a.elevationUnresolved=true; a.NotifyDataChanged(); Check(f.height.text=="-");
                a.elevationUnresolved=false; a.hasHeightAboveSurface=false; a.NotifyDataChanged(); Check(f.height.text=="-");
            }
        });
        Test("pointer placement queries a disabled map and preserves its hit normal",()=>
        {
            Collider c=Surface(false); c.query=(ray,d)=>new RaycastHit { point=new Vector3(1,2,3),normal=Vector3.up };
            RaycastHit hit; Check(GeographicSurfaceQuery.TryRaycast(c,new Ray(Vector3.zero,Vector3.up),10,~0,out hit));
            Check(hit.point.y==2 && hit.normal.y==1 && !c.enabled);
        });
        Test("pointer placement restores a disabled map after misses and exceptions",()=>
        {
            Collider c=Surface(false); RaycastHit hit;
            Check(!GeographicSurfaceQuery.TryRaycast(c,new Ray(Vector3.zero,Vector3.up),10,~0,out hit) && !c.enabled);
            c.query=(ray,d)=>{throw new InvalidOperationException("test");}; bool threw=false;
            try { GeographicSurfaceQuery.TryRaycast(c,new Ray(Vector3.zero,Vector3.up),10,~0,out hit); }
            catch(InvalidOperationException) { threw=true; }
            Check(threw && !c.enabled);
        });
        Test("a stroke owns draft nodes until it binds to a saved feature",()=>
        {
            GameObject go=new GameObject(); RuntimeLine line=go.AddComponent<RuntimeLine>();
            LineRenderer renderer=go.AddComponent<LineRenderer>(); line.Initialize(renderer,true);
            Check(line.IsDraft); line.AddNode(new GameObject().transform); Check(line.NodeCount==1);
            line.BindToFeature("feature_a"); Check(!line.IsDraft && line.FeatureId=="feature_a");
        });
        Test("releasing a drawing stroke removes its sibling nodes and owned material",()=>
        {
            foreach(bool play in new[] { false,true })
            {
                Application.isPlaying=play; GameObject go=new GameObject(), node=new GameObject();
                RuntimeLine line=go.AddComponent<RuntimeLine>(); LineRenderer renderer=go.AddComponent<LineRenderer>();
                Material material=new Material(); renderer.sharedMaterial=material;
                line.Initialize(renderer,true); line.AddNode(node.transform); line.ReleaseOwnedObjects();
                Check(node.destroyed && material.destroyed && line.NodeCount==0); line.ReleaseOwnedObjects();
            }
        });
        Test("a loaded line does not destroy nodes or a material it does not own",()=>
        {
            GameObject go=new GameObject(), node=new GameObject(); RuntimeLine line=go.AddComponent<RuntimeLine>();
            LineRenderer renderer=go.AddComponent<LineRenderer>(); Material material=new Material(); renderer.sharedMaterial=material;
            line.Initialize(renderer); line.AddNode(node.transform); line.ReleaseOwnedObjects();
            Check(!node.destroyed && !material.destroyed && !line.IsDraft);
        });
        Console.WriteLine("INTERACTION RESULT: "+passed+" passed, "+failed+" failed"); return failed==0?0:1;
    }
    private static Collider Surface(bool enabled)
    {
        Collider c=new GameObject().AddComponent<Collider>(); c.enabled=enabled; return c;
    }
    private static bool Query(Collider c,out Vector3 hit) { return GeographicSurfaceQuery.TryHit(c,Vector3.zero,Vector3.up,1,10,~0,out hit); }
    private sealed class PanelFixture:IDisposable
    {
        public readonly PointInfoPanel panel=new GameObject().AddComponent<PointInfoPanel>();
        public readonly TextMeshProUGUI source=new GameObject().AddComponent<TextMeshProUGUI>();
        public readonly TextMeshProUGUI height=new GameObject().AddComponent<TextMeshProUGUI>();
        public PanelFixture()
        {
            Set(panel,"sourceText",source); Set(panel,"altitudeValueText",height); Set(panel,"fadeDuration",0f);
            Invoke(panel,"Awake"); Invoke(panel,"OnEnable");
        }
        public PointData Register(string id)
        {
            PointData p=new GameObject().AddComponent<PointData>(); p.pointID=id; p.pointClass="animal"; panel.RegisterMarker(p,null); return p;
        }
        public void Dispose() { Invoke(panel,"OnDisable"); Invoke(panel,"OnDestroy"); }
    }
    private static void Invoke(object target,string method) { target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,null); }
    private static void Set(object target,string field,object value) { target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
    private static void Check(bool value) { if(!value) throw new Exception("Assertion failed"); }
    private static void Test(string name,Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS "+name); }
        catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex); }
    }
}

