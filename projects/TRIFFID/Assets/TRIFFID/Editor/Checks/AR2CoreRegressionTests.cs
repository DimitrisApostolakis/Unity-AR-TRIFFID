using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class AR2CoreRegressionTests
{
    private static int passed, failed;
    private static readonly DateTime Now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public static int Run()
    {
        passed = failed = 0;
        Test("local create and outbox survive an actual disk restart", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            Root restored = Restart(root);
            Check(restored.features.Count == 1 && restored.syncState.pending.Count == 1);
            Check(GeoJsonOutbox.Next(restored, Now).kind == "create");
        });
        Test("deletion tombstone and last content survive restart", () =>
        {
            Root root = Clean(Point("a", 1)); Delete(root, "a"); root = Restart(root);
            Check(root.features.Count == 0 && root.syncState.pending.Count == 1);
            GeoJsonPendingEdit next = GeoJsonOutbox.Next(root, Now);
            Check(next.kind == "delete" && next.payload.id == "a");
        });
        Test("create deleted before dispatch does not make a server request", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            Delete(root, "feature_a"); Check(root.syncState.pending.Count == 0);
        });
        Test("late update acknowledgement cannot clear a newer revision", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2);
            GeoJsonPendingEdit old = Dispatch(root); Change(root, "a", 3);
            long revision = GeoJsonOutbox.Find(root, "a").revision;
            GeoJsonOutbox.Acknowledge(root, old, "a");
            Check(root.syncState.pending.Count == 1 && GeoJsonOutbox.Find(root, "a").revision == revision);
            Equal(X(GeoJsonOutbox.Next(root, Now).payload), 3d);
        });
        Test("late create acknowledgement binds its server ID and queues the newer content", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonPendingEdit old = Dispatch(root); Change(root, "feature_a", 3);
            GeoJsonOutbox.Acknowledge(root, old, "server_42");
            GeoJsonPendingEdit next = GeoJsonOutbox.Next(root, Now);
            Check(next.kind == "update" && root.features[0].id == "feature_a");
            Check(GeoJsonOutbox.NetworkPayload(root, next).id == "server_42");
            Equal(X(next.payload), 3d);
        });
        Test("deletion during an in-flight create is retained after its acknowledgement", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonPendingEdit sent = Dispatch(root); Delete(root, "feature_a");
            GeoJsonOutbox.Acknowledge(root, sent, "server_42");
            Check(root.features.Count == 0 && GeoJsonOutbox.Next(root, Now).kind == "delete");
            Check(root.syncState.remoteIds["feature_a"] == "server_42");
        });
        Test("reverting while an update is in flight creates a compensating edit", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonPendingEdit sent = Dispatch(root);
            Change(root, "a", 1); GeoJsonOutbox.Acknowledge(root, sent, "a");
            Equal(X(GeoJsonOutbox.Next(root, Now).payload), 1d);
        });
        Test("restart requeues an in-flight update with the same operation ID", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonPendingEdit sent = Dispatch(root);
            root = Restart(root); GeoJsonPendingEdit next = GeoJsonOutbox.Next(root, Now);
            Check(next != null && next.operationId == sent.operationId && next.revision == sent.revision);
        });
        Test("restart reconciles an in-flight create instead of blindly duplicating it", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now); Dispatch(root);
            root = Restart(root); Check(GeoJsonOutbox.Next(root, Now) == null);
            Check(root.syncState.pending[0].status == "reconcile");
        });
        Test("lost create acknowledgement is recovered by echoed client identity", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonPendingEdit sent = Dispatch(root);
            Feature accepted = GeoJsonOutbox.NetworkPayload(root, sent); accepted.id = "server_99";
            GeoJsonOutbox.Fail(root, sent, "Lost response", 0, 1, 2, Now); root = Restart(root);
            GeoJsonOutbox.MergeRemote(root, Collection(accepted));
            Check(root.syncState.pending.Count == 0 && root.syncState.remoteIds["feature_a"] == "server_99");
            Check(root.features.Count == 1);
        });
        Test("lost create acknowledgement plus a newer edit produces an update", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonPendingEdit sent = Dispatch(root); Feature accepted = GeoJsonOutbox.NetworkPayload(root, sent); accepted.id = "server_99";
            Change(root, "feature_a", 4); GeoJsonOutbox.Fail(root, sent, "Lost response", 0, 1, 2, Now);
            root = Restart(root); GeoJsonOutbox.MergeRemote(root, Collection(accepted));
            GeoJsonPendingEdit next = GeoJsonOutbox.Next(root, Now);
            Check(next != null && next.kind == "update"); Equal(X(next.payload), 4d);
        });
        Test("absence from one GET does not prove an ambiguous create was never accepted", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonPendingEdit sent = Dispatch(root); GeoJsonOutbox.Fail(root, sent, "Timeout", 0, 1, 2, Now);
            GeoJsonOutbox.MergeRemote(root, Collection());
            Check(root.syncState.pending.Count == 1 && GeoJsonOutbox.Next(root, Now) == null);
        });
        Test("retry exhaustion retains the operation and makes a later attempt possible", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonPendingEdit sent = Dispatch(root);
            GeoJsonOutbox.Fail(root, sent, "Offline", 0, 1, 20, Now);
            Check(root.syncState.pending[0].status == "failed"); Check(GeoJsonOutbox.Next(root, Now) == null);
            Check(GeoJsonOutbox.Next(root, Now.AddSeconds(21)) != null);
        });
        Test("a failed feature does not block unrelated incoming robot features", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonPendingEdit sent = Dispatch(root);
            GeoJsonOutbox.Fail(root, sent, "Offline", 0, 1, 20, Now);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 1), Point("robot_b", 7)));
            Equal(X(root.features.Find(f => f.id == "a")), 2d);
            Check(root.features.Any(f => f.id == "robot_b") && root.syncState.pending.Count == 1);
        });
        Test("a delayed failure cannot poison a newer local update", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonPendingEdit sent = Dispatch(root); Change(root, "a", 3);
            GeoJsonOutbox.Fail(root, sent, "Old failure", 0, 1, 20, Now);
            Check(root.syncState.pending[0].status == "pending"); Equal(X(root.syncState.pending[0].payload), 3d);
        });
        Test("a competing server edit replaces a pending local edit", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 3), Point("robot", 5)));
            Check(root.syncState.pending.Count == 0); Equal(X(root.features.Find(f => f.id == "a")), 3d);
            Check(root.features.Any(f => f.id == "robot") && !GeoJsonOutbox.CaptureChanges(root, Now));
        });
        Test("accepting the server resolves a conflict with the chosen content", () =>
        {
            Root root = Empty(); root.features.Add(Point("a", 2)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 3)));
            Check(GeoJsonOutbox.Resolve(root, "a", false)); Equal(X(root.features[0]), 3d); Check(root.syncState.pending.Count == 0);
        });
        Test("keeping local resolves against the observed remote baseline", () =>
        {
            Root root = Empty(); root.features.Add(Point("a", 2)); GeoJsonOutbox.CaptureChanges(root, Now);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 3)));
            long previous = root.syncState.pending[0].revision; GeoJsonOutbox.Resolve(root, "a", true);
            Check(root.syncState.pending[0].revision > previous && GeoJsonOutbox.Next(root, Now).kind == "update");
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 3))); Check(root.syncState.pending[0].status == "pending");
        });
        Test("remote deletion cancels a pending local edit and removes the feature", () =>
        {
            Root root = Clean(Point("a", 1)); Change(root, "a", 2); GeoJsonOutbox.MergeRemote(root, Collection());
            Check(root.features.Count == 0 && root.syncState.pending.Count == 0 && root.syncState.remoteIds.Count == 0);
            Check(!GeoJsonOutbox.CaptureChanges(root, Now));
        });
        Test("remote deletion of a clean feature is applied", () =>
        { Root root = Clean(Point("a", 1)); GeoJsonOutbox.MergeRemote(root, Collection()); Check(root.features.Count == 0 && root.syncState.pending.Count == 0); });
        Test("already-deleted server feature acknowledges a local tombstone", () =>
        { Root root = Clean(Point("a", 1)); Delete(root, "a"); GeoJsonOutbox.MergeRemote(root, Collection()); Check(root.syncState.pending.Count == 0); });
        Test("a pending deletion remains recoverable locally", () =>
        { Root root = Clean(Point("a", 1)); Delete(root, "a"); Check(GeoJsonOutbox.RestoreDeletion(root, "a")); Check(root.features.Count == 1); });
        Test("old local snapshots accept differing server content without uploading the backup", () =>
        {
            Root root = Collection(Point("a", 2)); GeoJsonOutbox.Initialize(root, true);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 3)));
            Check(root.syncState.pending.Count == 0); Equal(X(root.features[0]), 3d);
        });
        Test("matching legacy snapshot becomes clean", () =>
        {
            Root root = Collection(Point("a", 2)); GeoJsonOutbox.Initialize(root, true);
            GeoJsonOutbox.MergeRemote(root, Collection(Point("a", 2))); Check(root.syncState.pending.Count == 0);
        });
        Test("duplicate client identities on the server require attention", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now); GeoJsonPendingEdit sent = Dispatch(root);
            Feature one = GeoJsonOutbox.NetworkPayload(root, sent), two = GeoJsonOutbox.Clone(one); one.id = "server_1"; two.id = "server_2";
            GeoJsonOutbox.MergeRemote(root, Collection(one, two)); Check(root.syncState.pending[0].status == "conflict");
        });
        Test("server identity and the local alias survive a competing server edit", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now); GeoJsonPendingEdit sent = Dispatch(root);
            GeoJsonOutbox.Acknowledge(root, sent, "server_1"); Change(root, "feature_a", 2);
            Feature remote = Point("server_1", 3); GeoJsonOutbox.MergeRemote(root, Collection(remote));
            Check(root.syncState.remoteIds["feature_a"] == "server_1" && root.features[0].id == "feature_a");
            Check(root.syncState.pending.Count == 0); Equal(X(root.features[0]), 3);
        });
        Test("feature network payload contains no local outbox or deletion records", () =>
        {
            Root root = Empty(); root.features.Add(Point("feature_a", 1)); GeoJsonOutbox.CaptureChanges(root, Now);
            string json = JsonConvert.SerializeObject(GeoJsonOutbox.NetworkPayload(root, GeoJsonOutbox.Next(root, Now)));
            Check(!json.Contains("_ar2Sync") && !json.Contains("operationId"));
        });
        Test("clockwise exterior reverses heights, sources, and coordinate references together", () =>
        {
            JArray ring = Ring(0,0, 0,1, 1,0); JArray originalSecond = (JArray)ring[1];
            JArray heights = new JArray(10,20,30), sources = new JArray("a","b","c"), statuses = new JArray("x","y","z"); string error;
            Check(GeoJsonPolygonValidator.TryNormalizeRing(ring, true, heights, sources, statuses, out error));
            Check(object.ReferenceEquals(ring[2], originalSecond)); Equal(heights[1].Value<double>(), 30d);
            Check(sources[1].Value<string>() == "c" && statuses[1].Value<string>() == "z");
            Check(JToken.DeepEquals(ring[0], ring[3]) && JToken.DeepEquals(heights[0], heights[3]));
        });
        Test("counterclockwise exterior remains counterclockwise", () =>
        { JArray ring = Ring(0,0, 1,0, 0,1); string error; Check(GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); Equal(((JArray)ring[1])[0].Value<double>(), 1d); });
        Test("hole order is clockwise", () =>
        { JArray ring = Ring(0,0, 1,0, 0,1); string error; Check(GeoJsonPolygonValidator.TryNormalizeRing(ring,false,out error)); Equal(((JArray)ring[1])[1].Value<double>(), 1d); });
        Test("ring closure copies the complete first coordinate exactly", () =>
        { JArray ring = Ring(23,38, 23.001,38, 23,38.001); ((JArray)ring[0])[2] = -2.5; string error; Check(GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); Check(JToken.DeepEquals(ring[0],ring[ring.Count-1])); });
        Test("crossing edges are rejected without modifying the unfinished ring", () =>
        { JArray ring = Ring(0,0, 1,1, 0,1, 1,0); string before = ring.ToString(); string error; Check(!GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); Check(before == ring.ToString()); });
        Test("zero-area polygon is rejected", () =>
        { JArray ring = Ring(0,0, 1,0, 2,0); string error; Check(!GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); });
        Test("repeated vertices are rejected", () =>
        { JArray ring = Ring(0,0, 1,0, 1,0, 0,1); string error; Check(!GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); });
        Test("non-finite coordinate is rejected", () =>
        { JArray ring = Ring(0,0, 1,0, 0,1); ((JArray)ring[1])[2] = double.NaN; string error; Check(!GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); });
        Test("nearby vertices are not mistaken for a closed ring by float rounding", () =>
        { JArray ring = Ring(23.70000,38.00000, 23.70001,38.00000, 23.70000,38.00001); string error; Check(GeoJsonPolygonValidator.TryNormalizeRing(ring,true,out error)); Check(ring.Count == 4); });
        Test("negative heights and their provenance survive serialization", () =>
        {
            Feature feature = Point("a",1); feature.properties.height_above_surface_m = -2.5f; feature.properties.heightSource = "mesh"; feature.properties.heightStatus = "below_surface";
            Feature copy = GeoJsonOutbox.Clone(feature); Equal(copy.properties.height_above_surface_m.Value,-2.5d); Check(copy.properties.heightStatus == "below_surface");
        });
        Test("atomic replacement keeps a readable previous backup", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ar2_atomic_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try { string path = Path.Combine(dir,"state.json"); GeoJsonAtomicFile.Write(path,"first"); GeoJsonAtomicFile.Write(path,"second"); Check(File.ReadAllText(path)=="second" && File.ReadAllText(path+".bak")=="first"); }
            finally { Directory.Delete(dir,true); }
        });
        Test("failed atomic write does not truncate the saved copy", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ar2_failure_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir,"state.json"); GeoJsonAtomicFile.Write(path,"saved"); Directory.CreateDirectory(path+".bak");
                bool threw = false; try { GeoJsonAtomicFile.Write(path,"new"); } catch(IOException) { threw=true; } catch(UnauthorizedAccessException) { threw=true; }
                Check(threw && File.ReadAllText(path)=="saved");
            }
            finally { Directory.Delete(dir,true); }
        });
        Test("create, update, and delete flow converges across disk restarts", () =>
        {
            Root root = Empty(); FakeServer server = new FakeServer(); root.features.Add(Point("feature_a",1)); GeoJsonOutbox.CaptureChanges(root,Now);
            Deliver(root,server); root=Restart(root); Change(root,"feature_a",4); root=Restart(root); Deliver(root,server);
            Equal(X(server.features[0]),4d); Delete(root,"feature_a"); root=Restart(root); Deliver(root,server);
            Check(server.features.Count==0 && root.features.Count==0 && root.syncState.pending.Count==0);
        });
        Test("lost update response does not turn our older accepted revision into a false conflict", () =>
        {
            Root root=Clean(Point("a",1)); Change(root,"a",2); GeoJsonPendingEdit sent=Dispatch(root);
            Feature accepted=GeoJsonOutbox.Clone(sent.payload); Change(root,"a",3);
            GeoJsonOutbox.Fail(root,sent,"Lost reply",0,1,2,Now);
            GeoJsonOutbox.MergeRemote(root,Collection(accepted));
            Check(root.syncState.pending[0].status=="pending"); Equal(X(root.syncState.pending[0].payload),3d);
        });
        Test("edits made after a legacy snapshot load are sent when the original baseline matches", () =>
        {
            Root root=Collection(Point("a",1)); GeoJsonOutbox.Initialize(root,true); Change(root,"a",2);
            GeoJsonOutbox.MergeRemote(root,Collection(Point("a",1)));
            Check(root.syncState.pending[0].status=="pending" && root.syncState.pending[0].kind=="update");
        });
        Test("an old conflict accepts the current server version automatically", () =>
        {
            Root root=Empty(); root.features.Add(Point("feature_a",1)); GeoJsonOutbox.CaptureChanges(root,Now);
            GeoJsonPendingEdit sent=Dispatch(root); GeoJsonOutbox.Acknowledge(root,sent,"server_1");
            Change(root,"feature_a",2); root.syncState.pending[0].status="conflict";
            GeoJsonOutbox.MergeRemote(root,Collection(Point("server_1",3)));
            Check(root.syncState.remoteIds["feature_a"]=="server_1" && root.syncState.pending.Count==0);
            Equal(X(root.features[0]),3);
        });
        Test("server deletion removes old conflicts and failed updates after a restart", () =>
        {
            foreach(string status in new[] { "pending", "conflict", "failed", "inflight", "reconcile" })
            {
                Root root=Clean(Point("a",1)); Change(root,"a",2); root.syncState.pending[0].status=status;
                root=Restart(root); GeoJsonOutbox.MergeRemote(root,Collection()); root=Restart(root);
                Check(root.features.Count==0 && root.syncState.pending.Count==0 && root.syncState.acknowledgedHashes.Count==0);
                Check(GeoJsonOutbox.Next(root,Now)==null && !GeoJsonOutbox.CaptureChanges(root,Now));
            }
        });
        Test("an empty server removes legacy backup features", () =>
        {
            Root root=Collection(Point("a",1)); GeoJsonOutbox.Initialize(root,true);
            GeoJsonOutbox.MergeRemote(root,Collection()); Check(root.features.Count==0 && root.syncState.pending.Count==0);
        });
        Test("server deletion retains a new annotation that has never been uploaded", () =>
        {
            Root root=Clean(Point("server_point",1)); root.features.Add(Point("new_annotation",2));
            GeoJsonOutbox.CaptureChanges(root,Now); GeoJsonOutbox.MergeRemote(root,Collection());
            Check(root.features.Count==1 && root.features[0].id=="new_annotation");
            Check(root.syncState.pending.Count==1 && GeoJsonOutbox.Next(root,Now).kind=="create");
        });
        Test("a competing server edit supersedes a pending local deletion", () =>
        {
            Root root=Clean(Point("a",1)); Delete(root,"a"); GeoJsonOutbox.MergeRemote(root,Collection(Point("a",3)));
            Check(root.syncState.pending.Count==0); Equal(X(root.features[0]),3);
        });
        Test("an invalid remote collection leaves the backup unchanged", () =>
        {
            Root root=Clean(Point("a",1)); Change(root,"a",2); string before=JsonConvert.SerializeObject(root);
            foreach(Root invalid in new[] { new Root(), Collection((Feature)null), Collection(new Feature()), Collection(Point("b",2),Point("b",3)) })
            {
                bool threw=false; try { GeoJsonOutbox.MergeRemote(root,invalid); } catch(ArgumentException) { threw=true; }
                catch(InvalidDataException) { threw=true; } catch(InvalidOperationException) { threw=true; }
                Check(threw && JsonConvert.SerializeObject(root)==before);
            }
        });
        Test("unsupported local-state schema fails without discarding the outbox", () =>
        {
            Root root=Empty(); root.syncState.schemaVersion=99; bool threw=false;
            try { GeoJsonOutbox.Initialize(root,true); } catch(InvalidDataException) { threw=true; }
            Check(threw && root.syncState.schemaVersion==99);
        });
        Test("duplicate local IDs are rejected instead of coalescing different features", () =>
        {
            Root root=Empty(); root.features.Add(Point("a",1)); root.features.Add(Point("a",2)); bool threw=false;
            try { GeoJsonOutbox.CaptureChanges(root,Now); } catch(InvalidDataException) { threw=true; }
            Check(threw && root.features.Count==2);
        });
        Test("an older asynchronous save cannot overwrite a newer synchronous flush", () =>
        {
            string dir=Path.Combine(Path.GetTempPath(),"ar2_sequence_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                string path=Path.Combine(dir,"state.json"); GeoJsonSaveSequence sequence=new GeoJsonSaveSequence();
                Check(sequence.Write(path,"newer",2)); Check(!sequence.Write(path,"older",1)); Check(File.ReadAllText(path)=="newer");
                Check(sequence.Write(path,"metadata at same revision",2)); Check(File.ReadAllText(path)=="metadata at same revision");
            }
            finally { Directory.Delete(dir,true); }
        });
        Test("API parser preserves a missing altitude instead of inventing zero", () =>
        {
            Feature feature=Point("a",1); feature.properties.altitude_m=null; feature.geometry.coordinates=new JArray(23d,38d);
            int dropped; Root parsed=GeoJsonPayloadParser.Parse(JsonConvert.SerializeObject(Collection(feature)),out dropped);
            Check(!parsed.features[0].properties.altitude_m.HasValue && ((JArray)parsed.features[0].geometry.coordinates).Count==2);
        });
        Test("API parser preserves an explicitly supplied zero altitude", () =>
        {
            Feature feature=Point("a",1); feature.properties.altitude_m=0;
            int dropped; Root parsed=GeoJsonPayloadParser.Parse(JsonConvert.SerializeObject(Collection(feature)),out dropped);
            Check(parsed.features[0].properties.altitude_m.HasValue); Equal(parsed.features[0].properties.altitude_m.Value,0);
        });
        Test("API parser retains negative heights, provenance, and client identity", () =>
        {
            Feature feature=Point("a",1); feature.properties.altitude_m=-3; feature.properties.height_above_surface_m=-2;
            feature.properties.heightSource="mesh"; feature.properties.clientFeatureId="feature_original";
            int dropped; Root parsed=GeoJsonPayloadParser.Parse(JsonConvert.SerializeObject(Collection(feature)),out dropped);
            Equal(parsed.features[0].properties.height_above_surface_m.Value,-2);
            Check(parsed.features[0].properties.heightSource=="mesh" && parsed.features[0].properties.clientFeatureId=="feature_original");
        });
        Test("New client identity uses ar_client_id while legacy records remain readable", () =>
        {
            Feature fresh=Point("local",1); fresh.properties.clientFeatureId="local";
            JObject output=JObject.FromObject(fresh);
            Check((string)output["properties"]["ar_client_id"]=="local");
            Check(output["properties"]["ar2_client_id"]==null);
            JObject legacy=(JObject)output.DeepClone();
            ((JObject)legacy["properties"]).Remove("ar_client_id");
            legacy["properties"]["ar2_client_id"]="local";
            Feature parsed=legacy.ToObject<Feature>();
            Check(parsed.properties.clientFeatureId=="local");
            Check(JToken.DeepEquals(legacy,JObject.FromObject(parsed)));
            Check(GeoJsonOutbox.Fingerprint(fresh)==GeoJsonOutbox.Fingerprint(parsed));
            JObject both=(JObject)legacy.DeepClone(); both["properties"]["ar_client_id"]="preferred";
            Check(both.ToObject<Feature>().properties.clientFeatureId=="preferred");
        });
        Test("API parser removes nonfinite optional altitude instead of accepting a height", () =>
        {
            JObject obj=JObject.FromObject(Collection(Point("a",1))); obj["features"][0]["properties"]["altitude_m"]="NaN";
            int dropped; Root parsed=GeoJsonPayloadParser.Parse(obj.ToString(),out dropped); Check(!parsed.features[0].properties.altitude_m.HasValue);
        });
        Test("API parser rejects a partial envelope instead of treating it as an empty server", () =>
        {
            int dropped; bool threw=false; try { GeoJsonPayloadParser.Parse("{\"error\":\"unavailable\"}",out dropped); } catch(JsonSerializationException) { threw=true; }
            Check(threw);
        });
        Test("API parser rejects a collection that would silently drop records without identifiers", () =>
        {
            JObject obj=JObject.FromObject(Collection(Point("a",1))); ((JObject)obj["features"][0]).Remove("id");
            int dropped; bool threw=false; try { GeoJsonPayloadParser.Parse(obj.ToString(),out dropped); } catch(JsonSerializationException) { threw=true; }
            Check(threw);
        });
        Test("visibility filters can be set before any server features arrive", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter(); filter.SetVisibility("animal",false);
            Feature animal=Point("animal_a",1); animal.properties.className="animal";
            Check(!filter.IsVisible(animal)); Check(filter.IsVisible(Point("citizen_a",2)));
        });
        Test("reloading serialized features does not reset a selected filter", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter(); filter.SetVisibility("animal",false);
            Feature animal=Point("a",1); animal.properties.className="animal";
            Root root=Collection(animal); string json=JsonConvert.SerializeObject(root);
            for(int i=0;i<3;i++)
            {
                root=JsonConvert.DeserializeObject<Root>(json); Check(!filter.IsVisible(root.features[0]));
                Check(JsonConvert.SerializeObject(root)==json);
            }
        });
        Test("new incoming features use the active class or category filter", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter(); filter.SetVisibility("animal",false);
            Feature byClass=Point("a",1), byCategory=Point("b",2);
            byClass.properties.className="animal"; byCategory.properties.category="animals";
            Check(!filter.IsVisible(byClass) && !filter.IsVisible(byCategory));
        });
        Test("enabling one category leaves other hidden categories unchanged", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter();
            Feature animal=Point("a",1), citizen=Point("b",2); animal.properties.className="animal"; citizen.properties.className="citizen";
            filter.SetVisibility("animal",false); filter.SetVisibility("citizen",false);
            filter.SetVisibility("animal",true); Check(filter.IsVisible(animal) && !filter.IsVisible(citizen));
        });
        Test("filter aliases and class prefixes retain their existing matching behavior", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter(); Feature animal=Point("a",1); animal.properties.className="Animal_Marker";
            filter.SetVisibility(" ANIMALS ",false); Check(!filter.IsVisible(animal));
            filter.SetVisibility("animal",true); Check(filter.IsVisible(animal));
            Feature city=Point("b",1); city.properties.category="city"; filter.SetVisibility("cities",false); Check(!filter.IsVisible(city));
        });
        Test("POI visibility rules preserve geometry scope and leave telemetry unrelated to the filter", () =>
        {
            GeoJsonVisibilityFilter filter=new GeoJsonVisibilityFilter(); filter.SetVisibility("animal",false);
            Feature feature=Point("a",1); feature.properties.className="animal";
            foreach(string type in new[] { "Point","MultiPoint","Polygon","MultiPolygon" })
            { feature.geometry.type=type; Check(!filter.IsVisible(feature)); }
            feature.geometry.type="LineString"; Check(filter.IsVisible(feature));
            feature.geometry.type="Point"; feature.properties.className="ugv"; feature.properties.category="telemetry";
            Check(filter.IsVisible(feature));
        });
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static Root Empty() { Root root=Collection(); GeoJsonOutbox.Initialize(root,false); return root; }
    private static Root Collection(params Feature[] features) { return new Root { features=new List<Feature>(features) }; }
    private static Root Clean(params Feature[] features) { Root root=Empty(); GeoJsonOutbox.MergeRemote(root,Collection(features)); return root; }
    private static Feature Point(string id, double x)
    {
        return new Feature { id=id, properties=new Properties { id=id, className="person", source="test", altitude_m=5 },
            geometry=new Geometry { type="Point", coordinates=new JArray(x,38d,5d) } };
    }
    private static double X(Feature f) { return ((JArray)f.geometry.coordinates)[0].Value<double>(); }
    private static void Change(Root root,string id,double x) { ((JArray)root.features.Find(f=>f.id==id).geometry.coordinates)[0]=x; GeoJsonOutbox.CaptureChanges(root,Now); }
    private static void Delete(Root root,string id) { GeoJsonOutbox.RecordDeletion(root,root.features.Find(f=>f.id==id)); root.features.RemoveAll(f=>f.id==id); GeoJsonOutbox.CaptureChanges(root,Now); }
    private static GeoJsonPendingEdit Dispatch(Root root) { GeoJsonPendingEdit next=GeoJsonOutbox.Next(root,Now); Check(next!=null); GeoJsonOutbox.MarkDispatched(root,next); return next; }
    private static Root Restart(Root root)
    {
        string dir=Path.Combine(Path.GetTempPath(),"ar2_restart_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { string path=Path.Combine(dir,"state.geojson"); GeoJsonAtomicFile.Write(path,JsonConvert.SerializeObject(root)); Root loaded=JsonConvert.DeserializeObject<Root>(File.ReadAllText(path)); GeoJsonOutbox.RestoreAfterRestart(loaded); return loaded; }
        finally { Directory.Delete(dir,true); }
    }
    private static JArray Ring(params double[] xy) { JArray ring=new JArray(); for(int i=0;i<xy.Length;i+=2) ring.Add(new JArray(xy[i],xy[i+1],5d)); return ring; }
    private static void Deliver(Root root,FakeServer server)
    {
        GeoJsonOutbox.MergeRemote(root,Collection(server.features.Select(GeoJsonOutbox.Clone).ToArray()));
        GeoJsonPendingEdit sent=Dispatch(root); string remoteId=null;
        if(sent.kind=="create") remoteId=server.Create(GeoJsonOutbox.NetworkPayload(root,sent));
        else { remoteId=root.syncState.remoteIds[sent.featureId]; if(sent.kind=="delete") server.features.RemoveAll(f=>f.id==remoteId); else { server.features.RemoveAll(f=>f.id==remoteId); server.features.Add(GeoJsonOutbox.NetworkPayload(root,sent)); } }
        GeoJsonOutbox.Acknowledge(root,sent,remoteId);
    }
    private sealed class FakeServer
    {
        public List<Feature> features=new List<Feature>();
        public string Create(Feature f) { f=GeoJsonOutbox.Clone(f); f.id="server_"+(features.Count+1); features.Add(f); return f.id; }
    }
    private static void Check(bool value) { if(!value) throw new Exception("Assertion failed"); }
    private static void Equal(double actual,double expected) { if(Math.Abs(actual-expected)>1e-8) throw new Exception("Expected "+expected+", got "+actual); }
    private static void Test(string name,Action action) { try { action(); passed++; Console.WriteLine("PASS "+name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex); } }
}

