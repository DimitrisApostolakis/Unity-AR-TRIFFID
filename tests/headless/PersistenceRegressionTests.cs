using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class PersistenceRegressionTests
{
    private static int passed;
    private static readonly string Foreign = @"{""type"":""Feature"",""id"":772,""bbox"":[23,38,24,39],""vendor"":{""stamp"":""2026-10-26T00:00:00+02:00""},""properties"":{""class"":""Unsupported UAV"",""id"":""Robot_ABC"",""source"":""Mission A"",""confidence"":0.12345678912345,""altitude_m"":102.123456789,""extra"":{""values"":[1,null,true]}},""geometry"":{""type"":""Point"",""coordinates"":[23.12345678912345,38.98765432198765,102.123456789],""vendor-geometry"":true}}";
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static Feature Read(string text) { return JsonConvert.DeserializeObject<Feature>(text); }
    private static JObject Json(object value) { return JObject.Parse(JsonConvert.SerializeObject(value)); }
    private static Root RootFor(params Feature[] features)
    {
        var root = new Root { features = new List<Feature>(features) };
        GeoJsonOutbox.Initialize(root, true); return root;
    }
    private static string FixtureFile;
    public static int Main(string[] args)
    {
        string directory=Path.Combine(Path.GetTempPath(),"ar2-persistence-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        FixtureFile=Path.Combine(directory,"state.json");
        try
        {
            Test("foreign feature preserves IDs, precision, absent fields, geometry and nested extras", () =>
            { Check(JToken.DeepEquals(JObject.Parse(Foreign), Json(Read(Foreign)))); });
            Test("API parse and remote merge retain complete unsupported feature", () =>
            {
                int ignored; Root remote=GeoJsonPayloadParser.Parse("{\"type\":\"FeatureCollection\",\"features\":["+Foreign+"]}",out ignored);
                Root local=RootFor(); GeoJsonOutbox.MergeRemote(local,remote);
                Check(JToken.DeepEquals(Json(local.features[0]),JObject.Parse(Foreign)));
                Check(!GeoJsonOutbox.CaptureChanges(local,DateTime.UtcNow) && local.syncState.pending.Count==0);
            });
            Test("loading a legacy snapshot creates no operations and edits still reconcile", () =>
            {
                Root root=RootFor(Read(Foreign)); Check(root.syncState.nextRevision==1 && root.syncState.pending.Count==0);
                Check(!GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow));
                ((JArray)root.features[0].geometry.coordinates)[2]=103;
                Check(GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow));
                Check(root.syncState.pending[0].kind=="reconcile" && GeoJsonOutbox.Next(root,DateTime.UtcNow)==null);
            });
            Test("new feature, edit, deletion and unrelated foreign data persist together", () =>
            {
                Feature foreign=Read(Foreign), created=Read(Foreign); created.id="new";
                Root root=RootFor(foreign); root.features.Add(created); GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow);
                Check(root.syncState.pending.Count==1 && root.syncState.pending[0].kind=="create");
                Check(JToken.DeepEquals(Json(foreign),JObject.Parse(Foreign)));
                GeoJsonOutbox.RecordDeletion(root,created);root.features.Remove(created);GeoJsonOutbox.CaptureChanges(root,DateTime.UtcNow);
                Check(root.syncState.pending.Count==0);
            });
            Test("a point edit changes only its coordinates and requested field", () =>
            {
                Feature feature=Read(Foreign); JObject expected=JObject.Parse(Foreign);
                string error; Check(GeoJsonVertexEdit.TryApply(feature,new[]{(JArray)feature.geometry.coordinates},23.5,38.5,105,out error));
                expected["geometry"]["coordinates"]=new JArray(23.5,38.5,105d);
                Check(JToken.DeepEquals(expected,Json(feature)));
            });
            Test("one line vertex edit preserves all other coordinates and height entries", () =>
            {
                Feature feature=Read(@"{""id"":""Line_A"",""geometry"":{""type"":""LineString"",""coordinates"":[[23,38,10],[23.01,38.01,11],[23.02,38.02,12]]},""properties"":{""class"":""road"",""heights_above_surface_m"":[1.123456789,2,3]}}");
                JArray vertices=(JArray)feature.geometry.coordinates; JToken first=vertices[0].DeepClone(), last=vertices[2].DeepClone();string error;
                Check(GeoJsonVertexEdit.TryApply(feature,new[]{(JArray)vertices[1]},23.011,38.011,15,out error));
                feature.properties.heights_above_surface_m=GeoJsonVertexEdit.SetVertexValue(feature.properties.heights_above_surface_m,vertices,(JArray)vertices[1],new JValue(7));
                Check(JToken.DeepEquals(vertices[0],first)&&JToken.DeepEquals(vertices[2],last));
                Check(feature.properties.heights_above_surface_m[0].Value<double>()==1.123456789 && feature.properties.heights_above_surface_m[2].Value<int>()==3);
            });
            Test("polygon closure aliases move together without winding or sibling changes", () =>
            {
                Feature feature=Polygon();JArray ring=(JArray)feature.geometry.coordinates[0];JToken untouched=ring[1].DeepClone();string error;
                Check(GeoJsonVertexEdit.TryApply(feature,new[]{(JArray)ring[0],(JArray)ring[4]},23.001,38.001,20,out error));
                Check(JToken.DeepEquals(ring[0],ring[4])&&JToken.DeepEquals(ring[1],untouched));
            });
            Test("invalid polygon edits roll back coordinates and keep tuple bindings", () =>
            {
                Feature feature=Polygon();JArray ring=(JArray)feature.geometry.coordinates[0], vertex=(JArray)ring[1];string before=Json(feature).ToString(), error;
                Check(!GeoJsonVertexEdit.TryApply(feature,new[]{vertex},23,38.01,10,out error));
                Check(before==Json(feature).ToString()&&ReferenceEquals(vertex,ring[1]));
            });
            Test("100 production flush/reload cycles make zero conversions and zero operations", () =>
            {
                Root root=RootFor(Read(Foreign));
                for(int i=0;i<100;i++)
                {
                    JsonSpawner spawner=Spawner(root); spawner.FlushFixture(); Check(spawner.conversions==0);
                    root=JsonConvert.DeserializeObject<Root>(File.ReadAllText(FixtureFile));
                    Check(JToken.DeepEquals(Json(root.features[0]),JObject.Parse(Foreign)));
                    Check(root.syncState.pending.Count==0&&root.syncState.nextRevision==1);
                }
            });
            Test("map movement without an edit cannot commit scene positions", () =>
            {
                Root root=RootFor(Read(Foreign)); JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                spawner.mapTransform.localPosition=new Vector3(10000,10000,-10000);
                Check(!spawner.SyncData(node.GetComponent<PointData>(),node));spawner.FlushFixture();
                Check(spawner.conversions==0&&root.syncState.pending.Count==0);
            });
            Test("select and release without movement leaves authoritative doubles unchanged", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                spawner.BeginFeatureEdit(node); node.GetComponent<PointData>().longitude=999;
                Check(!spawner.SyncData(node.GetComponent<PointData>(),node));
                Check(node.GetComponent<PointData>().longitude==23.12345678912345&&spawner.conversions==0);
            });
            Test("release commits once and repeat release or save cannot reconvert", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                root.syncState.remoteIds[root.features[0].id]=root.features[0].id;
                spawner.BeginFeatureEdit(node);node.localPosition=new Vector3(23.5f,38.5f,105);
                Check(spawner.SyncData(node.GetComponent<PointData>(),node));Check(spawner.conversions==1);
                Check(!spawner.SyncData(node.GetComponent<PointData>(),node));spawner.FlushFixture();
                Check(spawner.conversions==1&&root.syncState.pending.Count==1);
                long revision=root.syncState.nextRevision;spawner.FlushFixture();Check(root.syncState.nextRevision==revision);
                Root saved=JsonConvert.DeserializeObject<Root>(File.ReadAllText(FixtureFile));
                Check(saved.features[0].geometry.coordinates[2].Value<double>()==105);
            });
            Test("cancelled edit cannot later enter persistence", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                spawner.BeginFeatureEdit(node);node.localPosition=new Vector3(23.5f,38.5f,105);spawner.CancelFeatureEdit(node);
                Check(!spawner.SyncData(node.GetComponent<PointData>(),node));spawner.FlushFixture();
                Check(spawner.conversions==0&&root.syncState.pending.Count==0);
            });
            Test("FloatingIcon release writes the deliberate position through the async save path", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                FloatingIcon icon=node.gameObject.AddComponent<FloatingIcon>();icon.Setup(spawner.mapTransform,spawner);
                icon.OnSelectEntered(null);node.localPosition=new Vector3(23.6f,38.6f,107);icon.OnManipulationEnded(null);
                Check(System.Threading.SpinWait.SpinUntil(()=>!spawner.HasPendingLocalPersistence,5000));
                Check(spawner.conversions==1&&File.Exists(FixtureFile));icon.OnManipulationEnded(null);Check(spawner.conversions==1);
                Root stored=JsonConvert.DeserializeObject<Root>(File.ReadAllText(FixtureFile));
                Check(stored.features[0].geometry.coordinates[2].Value<double>()==107);
            });
            Test("FloatingIcon selection without moving preserves its anchor and creates no save", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                FloatingIcon icon=node.gameObject.AddComponent<FloatingIcon>();icon.Setup(spawner.mapTransform,spawner);
                Vector3 anchor=icon.localMapPoint;icon.OnSelectEntered(null);icon.OnManipulationEnded(null);
                Check(icon.localMapPoint.Equals(anchor)&&spawner.conversions==0&&!spawner.HasPendingLocalPersistence);
            });
            Test("selected-state fallback uses the same one-time release commit", () =>
            {
                Root root=RootFor(Read(Foreign));JsonSpawner spawner=Spawner(root);Transform node=Node(spawner,root.features[0]);
                var grab=node.gameObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
                FloatingIcon icon=node.gameObject.AddComponent<FloatingIcon>();icon.Setup(spawner.mapTransform,spawner);
                var update=typeof(FloatingIcon).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                grab.isSelected=true;update.Invoke(icon,null);node.localPosition=new Vector3(23.7f,38.7f,108);
                grab.isSelected=false;update.Invoke(icon,null);
                Check(System.Threading.SpinWait.SpinUntil(()=>!spawner.HasPendingLocalPersistence,5000));Check(spawner.conversions==1);
            });
            if (args.Length > 0) Test("supplied capture fixture retains every feature across 100 production saves", () =>
            {
                Root root=JsonConvert.DeserializeObject<Root>(File.ReadAllText(args[0]));GeoJsonOutbox.Initialize(root,true);
                string original=JsonConvert.SerializeObject(root.features);
                for (int cycle=0;cycle<100;cycle++)
                {
                    JsonSpawner spawner=Spawner(root);spawner.FlushFixture();Check(spawner.conversions==0);
                    root=JsonConvert.DeserializeObject<Root>(File.ReadAllText(FixtureFile));
                    Check(JsonConvert.SerializeObject(root.features)==original&&root.syncState.pending.Count==0&&root.syncState.nextRevision==1);
                }
            });
            Console.WriteLine("PERSISTENCE RESULT: "+passed+" passed, 0 failed (engine doubles, no native scene measurement)");return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
        finally { Directory.Delete(directory,true); }
    }
    private static Feature Polygon()
    { return Read(@"{""id"":""P"",""geometry"":{""type"":""Polygon"",""coordinates"":[[[23,38,10],[23.01,38,10],[23.01,38.01,10],[23,38.01,10],[23,38,10]]]},""properties"":{""class"":""safe""}}"); }
    private static JsonSpawner Spawner(Root root)
    { var spawner=new GameObject().AddComponent<JsonSpawner>();spawner.mapTransform=new GameObject().transform;spawner.Fixture(root,FixtureFile);return spawner; }
    private static Transform Node(JsonSpawner spawner,Feature feature)
    {
        var node=new GameObject();node.transform.parent=spawner.mapTransform;node.transform.localPosition=new Vector3(23.123457f,38.987656f,102.12346f);
        PointData data=node.AddComponent<PointData>();data.pointID=feature.id;data.longitude=feature.geometry.coordinates[0].Value<double>();data.latitude=feature.geometry.coordinates[1].Value<double>();data.altitude=feature.geometry.coordinates[2].Value<double>();
        spawner.Bind(node.transform,feature,(JArray)feature.geometry.coordinates);return node.transform;
    }
}
