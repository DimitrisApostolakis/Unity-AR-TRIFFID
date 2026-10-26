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

// Native Unity scene measurements. Never substitutes matrix arithmetic or the save pipeline.
public static class SceneConsistencyCollector
{
    public const string RawHeader = "phase,condition,scope,step,cycle,feature_id,vertex_path,geometry_type,local_x,local_y,local_z,world_x,world_y,world_z,inverse_x,inverse_y,inverse_z,lon,lat,height,stored_lon,stored_lat,stored_height,baseline_lon,baseline_lat,baseline_height";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [MenuItem("Tools/Experiments/Scene Consistency/Run Pilot (100 Cycles)")]
    private static void RunPilot() { ChooseAndRun(100); }

    [MenuItem("Tools/Experiments/Scene Consistency/Run Extended (1000 Cycles)")]
    private static void RunExtended() { ChooseAndRun(1000); }

    private static void ChooseAndRun(int cycles)
    {
        try
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Start the scene, let loading/autosave finish, then PAUSE Play mode before running this scene experiment.");
            JsonSpawner selected = Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponent<JsonSpawner>();
            JsonSpawner[] candidates = Resources.FindObjectsOfTypeAll<JsonSpawner>()
                .Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy).ToArray();
            JsonSpawner source = selected != null ? selected : candidates.Length == 1 ? candidates[0] : null;
            if (source == null) throw new InvalidOperationException("Select the loaded JsonSpawner GameObject in the Hierarchy.");
            string directory = EditorUtility.OpenFolderPanel("Select PreparedSceneInputs (contains prepared_scene.json)", "", "");
            if (string.IsNullOrEmpty(directory)) return;
            string capture = Run(directory, source, cycles);
            Debug.Log("[SceneConsistency] Capture complete. Play mode remains paused. Results: " + capture +
                "\nAnalyze: python LocalScripts/run_scene_consistency.py --analyze-run \"" + capture + "\"");
            EditorUtility.RevealInFinder(capture);
        }
        catch (Exception error) { Debug.LogException(error); }
    }

    public static string Run(string preparedDirectory, JsonSpawner source, int maxCycles)
    {
        if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
            throw new InvalidOperationException("A paused, loaded Play-mode scene is required.");
        if (source == null || maxCycles != 100 && maxCycles != 1000) throw new ArgumentException("Use a loaded JsonSpawner and 100 or 1000 cycles.");
        if ((bool)SceneConsistencySession.Get(source, "saveInProgress") || (bool)SceneConsistencySession.Get(source, "saveQueued") ||
            (int)SceneConsistencySession.Get(source, "dataVersion") != (int)SceneConsistencySession.Get(source, "lastSavedVersion"))
            throw new InvalidOperationException("Local saving has not settled. Unpause, finish/cancel drawing and let autosave finish, then pause again.");
        var sourceData = (Root)SceneConsistencySession.Get(source, "currentRootData");
        var config = (TransformConfig)SceneConsistencySession.Get(source, "transformData");
        if (sourceData == null || config == null) throw new InvalidOperationException("The selected JsonSpawner has not finished loading.");
        foreach (RuntimeLine stroke in (IEnumerable<RuntimeLine>)SceneConsistencySession.Get(source, "runtimeAnnotationStrokes"))
            if (stroke != null && stroke.IsDraft && stroke.NodeCount > 0) throw new InvalidOperationException("Finish/cancel the draft drawing before the experiment.");
        Transform map = source.mapTransform != null ? source.mapTransform : source.transform;
        if (Resources.FindObjectsOfTypeAll<FloatingIcon>().Any(x => x.mainMap == map && x.IsBeingManipulated))
            throw new InvalidOperationException("Release all map markers before the experiment.");

        if (!File.Exists(Path.Combine(preparedDirectory, "prepared_scene.json")))
        {
            string child = Path.Combine(preparedDirectory, "PreparedSceneInputs");
            if (File.Exists(Path.Combine(child, "prepared_scene.json"))) preparedDirectory = child;
            else throw new DirectoryNotFoundException("Select PreparedSceneInputs, containing prepared_scene.json, plan.json and fixture.geojson.");
        }
        JObject prepared = JObject.Parse(File.ReadAllText(Path.Combine(preparedDirectory, "prepared_scene.json")));
        if (prepared.Value<int>("schema_version") != 1) throw new InvalidDataException("Unsupported prepared scene version.");
        foreach (JProperty file in ((JObject)prepared["files_sha256"]).Properties())
            if (Hash(Path.Combine(preparedDirectory, file.Name)) != file.Value.Value<string>()) throw new InvalidDataException("Prepared input changed: " + file.Name);
        var sourceHashes = new JObject();
        foreach (JProperty file in ((JObject)prepared["sources_lf_sha256"]).Properties())
        {
            string hash = SourceHash(Path.Combine(Application.dataPath, file.Name));
            sourceHashes[file.Name] = hash;
            if (hash != file.Value.Value<string>()) throw new InvalidDataException("Source differs from preparation: " + file.Name + ". Pull the experiments branch or prepare a new folder.");
        }
        TransformConfig expectedConfig = JsonConvert.DeserializeObject<TransformConfig>(File.ReadAllText(Path.Combine(preparedDirectory, "alignment.json")));
        if (!SameConfig(config, expectedConfig)) throw new InvalidDataException("The loaded scene uses a different calibration from PreparedSceneInputs.");
        var pose = new SavedPose(map);
        var flags = map.GetComponentsInChildren<Transform>(true).ToDictionary(x => x, x => x.hasChanged);
        for (Transform parent = map.parent; parent != null; parent = parent.parent) flags[parent] = parent.hasChanged;
        var warningFlags = typeof(FloatingIcon).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(x => x.FieldType == typeof(bool) && x.Name.StartsWith("missing", StringComparison.Ordinal))
            .ToDictionary(x => x, x => (bool)x.GetValue(null));
        string sourceJson = JsonConvert.SerializeObject(sourceData);
        string originalFile = source.saveFilePath;
        string originalFileHash = File.Exists(originalFile) ? Hash(originalFile) : null;
        string capture = Path.Combine(preparedDirectory, "Capture_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", Invariant) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(capture);
        foreach (string name in new[] { "prepared_scene.json", "alignment.json", "fixture.geojson", "plan.json", "expected.csv" })
            File.Copy(Path.Combine(preparedDirectory, name), Path.Combine(capture, name));
        JObject plan = JObject.Parse(File.ReadAllText(Path.Combine(capture, "plan.json")));
        var matrices = new JArray();
        JObject metadata = new JObject
        {
            ["schema_version"] = 1, ["completed"] = false, ["source_state_restored"] = false,
            ["profile"] = "Unity Editor " + Application.unityVersion, ["max_cycles"] = maxCycles,
            ["started_utc"] = DateTime.UtcNow.ToString("O", Invariant), ["source_scene"] = source.gameObject.scene.path,
            ["source_spawner"] = HierarchyPath(source.transform), ["map_transform"] = HierarchyPath(map),
            ["calibration_float"] = JObject.FromObject(config), ["source_hashes_lf"] = sourceHashes,
            ["source_save_sha256_before"] = originalFileHash, ["hierarchy"] = Hierarchy(map),
            ["network_uploads"] = "not measured; disconnected sandbox has no coordinator or API manager",
            ["surface_height"] = "not measured; fixture has a configured target without a collider",
            ["persistence_path"] = "JsonSpawner synchronous flush + atomic file + SpawnFromJson reload; normal asynchronous save timing is not measured",
            ["baseline"] = "Initial load and one warmup save/reload are recorded separately; synthetic acknowledgement is applied only to the disconnected fixture. No repeated settling or acknowledgement after measured cycles.",
            ["fixture"] = "Minimal inactive prefabs under an identity child of the actual map hierarchy. No interaction or billboard animation. Actual user marker coordinates/data are read only.",
            ["limits"] = "Pilot transforms and synthetic fixtures do not replace a manual controller/hand replay, actual network no-op trial, height or geographic accuracy measurements."
        };
        int rawRows = 0;
        LogType logFilter = Debug.unityLogger.filterLogType;
        try
        {
            // Avoid thousands of informational setup logs; warnings and errors remain visible.
            Debug.unityLogger.filterLogType = LogType.Warning;
            using (var raw = new StreamWriter(Path.Combine(capture, "scene_raw.csv")))
            using (var saves = new StreamWriter(Path.Combine(capture, "save_events.csv")))
            using (var edits = new StreamWriter(Path.Combine(capture, "outbox_events.jsonl")))
            {
                raw.WriteLine(RawHeader);
                saves.WriteLine("phase,condition,scope,cycle,new_operations,pending_operations,changed_feature_semantics,next_revision");
                using (var session = new SceneConsistencySession(map, Path.Combine(capture, "alignment.json"), Path.Combine(capture, "fixture.geojson"), Path.Combine(capture, "State_manipulation")))
                {
                    session.AcknowledgeFixture();
                    var initial = Baselines(session);
                    rawRows += Record(raw, session, session.Data, initial, "initial_load", "manipulation", "normal", 0, 0, prepared);
                    Save(session, saves, edits, "warmup", "manipulation", "normal", 0);
                    session.Reload();
                    rawRows += Record(raw, session, session.Data, initial, "warmup", "manipulation", "normal", 0, 0, prepared);
                    session.AcknowledgeFixture();
                    var baseline = Baselines(session);
                    int step = 0;
                    foreach (JObject condition in (JArray)plan["poses"])
                    {
                        Progress("Map manipulation: " + condition.Value<string>("id"), step / (float)((JArray)plan["poses"]).Count);
                        pose.Apply(map, condition, config);
                        matrices.Add(MatrixRecord(map, condition, config, "pose"));
                        Root stored = Save(session, saves, edits, "pose", condition.Value<string>("id"), condition.Value<string>("scope"), 0);
                        rawRows += Record(raw, session, stored, baseline, "pose", condition.Value<string>("id"), condition.Value<string>("scope"), step++, 0, prepared);
                    }
                }
                int trial = 0;
                foreach (JObject condition in (JArray)plan["persistence_trials"])
                {
                    pose.Apply(map, condition, config);
                    string id = condition.Value<string>("id"), scope = condition.Value<string>("scope");
                    matrices.Add(MatrixRecord(map, condition, config, "cycle"));
                    using (var session = new SceneConsistencySession(map, Path.Combine(capture, "alignment.json"), Path.Combine(capture, "fixture.geojson"), Path.Combine(capture, "State_" + id)))
                    {
                        session.AcknowledgeFixture();
                        var initial = Baselines(session);
                        rawRows += Record(raw, session, session.Data, initial, "cycle_initial", id, scope, trial, 0, prepared);
                        Save(session, saves, edits, "cycle_warmup", id, scope, 0);
                        session.Reload();
                        rawRows += Record(raw, session, session.Data, initial, "cycle_warmup", id, scope, trial, 0, prepared);
                        session.AcknowledgeFixture();
                        var baseline = Baselines(session);
                        rawRows += Record(raw, session, session.Data, baseline, "cycle", id, scope, trial, 0, prepared);
                        File.Copy(session.SavePath, Path.Combine(Path.GetDirectoryName(session.SavePath), "checkpoint_0.json"));
                        for (int cycle = 1; cycle <= maxCycles; cycle++)
                        {
                            if (cycle % 5 == 1) Progress(id + ": unedited save/reload " + cycle + "/" + maxCycles, cycle / (float)maxCycles);
                            Root stored = Save(session, saves, edits, "cycle", id, scope, cycle);
                            if (new[] { 1, 10, 100, 1000 }.Contains(cycle))
                                File.Copy(session.SavePath, Path.Combine(Path.GetDirectoryName(session.SavePath), "checkpoint_" + cycle + ".json"));
                            session.Reload();
                            rawRows += Record(raw, session, stored, baseline, "cycle", id, scope, trial, cycle, prepared);
                        }
                    }
                    trial++;
                }
            }
            metadata["completed"] = true;
        }
        catch (Exception error)
        {
            metadata["error"] = error.ToString();
            throw;
        }
        finally
        {
            pose.Restore(map);
            foreach (var pair in flags) if (pair.Key != null) pair.Key.hasChanged = pair.Value;
            foreach (var pair in warningFlags) pair.Key.SetValue(null, pair.Value);
            Debug.unityLogger.filterLogType = logFilter;
            EditorUtility.ClearProgressBar();
            bool restored = pose.Matches(map) && JsonConvert.SerializeObject(SceneConsistencySession.Get(source, "currentRootData")) == sourceJson &&
                (File.Exists(originalFile) ? Hash(originalFile) : null) == originalFileHash;
            metadata["source_state_restored"] = restored;
            metadata["source_save_sha256_after"] = File.Exists(originalFile) ? Hash(originalFile) : null;
            metadata["raw_rows"] = rawRows;
            metadata["finished_utc"] = DateTime.UtcNow.ToString("O", Invariant);
            WriteJson(Path.Combine(capture, "pose_matrices.json"), matrices);
            var hashes = new JObject();
            foreach (string name in new[] { "scene_raw.csv", "save_events.csv", "outbox_events.jsonl", "pose_matrices.json" })
                if (File.Exists(Path.Combine(capture, name))) hashes[name] = Hash(Path.Combine(capture, name));
            metadata["output_sha256"] = hashes;
            WriteJson(Path.Combine(capture, "scene_runtime.json"), metadata);
        }
        if (!metadata.Value<bool>("source_state_restored")) throw new InvalidOperationException("Source state comparison failed. Retain the capture as failure evidence: " + capture);
        return capture;
    }

    private static Root Save(SceneConsistencySession session, StreamWriter saves, StreamWriter edits, string phase, string condition, string scope, int cycle)
    {
        long before = session.Data.syncState.nextRevision;
        Root stored = session.Save();
        var state = stored.syncState;
        int changed = stored.features.Count(f => !state.acknowledgedHashes.ContainsKey(f.id) || state.acknowledgedHashes[f.id] != GeoJsonOutbox.Fingerprint(f));
        saves.WriteLine(string.Join(",", new object[] { phase, condition, scope, cycle, state.nextRevision - before, state.pending.Count, changed, state.nextRevision }));
        foreach (GeoJsonPendingEdit edit in state.pending.Where(x => x.revision >= before))
            edits.WriteLine(JsonConvert.SerializeObject(new { phase, condition, scope, cycle, edit }));
        return stored;
    }

    private static Dictionary<string, double[]> Baselines(SceneConsistencySession session)
    { return session.Nodes().ToDictionary(x => x.Key, session.Geographic); }

    private static int Record(StreamWriter output, SceneConsistencySession session, Root stored,
        Dictionary<string, double[]> baseline, string phase, string condition, string scope, int step, int cycle, JObject prepared)
    {
        List<SceneConsistencySession.Node> nodes = session.Nodes();
        if (nodes.Count != prepared.Value<int>("coordinate_entries")) throw new InvalidDataException("Unexpected fixture vertex count.");
        foreach (var node in nodes)
        {
            Vector3 local = session.Local(node), world = node.transform.position, inverse = session.Map.InverseTransformPoint(world);
            double[] geo = session.Geographic(node);
            Feature feature = stored.features.Single(x => x.id == node.feature.id);
            JToken token = feature.geometry.coordinates;
            if (node.path.Length != 0) foreach (string index in node.path.Split('/')) token = token[int.Parse(index, Invariant)];
            double[] persisted = ((JArray)token).Select(x => x.Value<double>()).ToArray();
            if (persisted.Length != 3 || !baseline.ContainsKey(node.Key)) throw new InvalidDataException("Coordinate identity changed.");
            output.Write(string.Join(",", new object[] { phase, condition, scope, step, cycle, node.feature.id, node.path, node.feature.geometry.type }));
            foreach (double value in new[] { (double)local.x, local.y, local.z, world.x, world.y, world.z, inverse.x, inverse.y, inverse.z }
                .Concat(geo).Concat(persisted).Concat(baseline[node.Key])) output.Write("," + value.ToString("R", Invariant));
            output.WriteLine();
        }
        return nodes.Count;
    }

    private sealed class SavedPose
    {
        private readonly Vector3 position, scale;
        private readonly Quaternion rotation;
        internal SavedPose(Transform target) { position = target.localPosition; rotation = target.localRotation; scale = target.localScale; }
        internal void Restore(Transform target) { target.localPosition = position; target.localRotation = rotation; target.localScale = scale; }
        internal bool Matches(Transform target) { return target.localPosition.Equals(position) && target.localRotation.Equals(rotation) && target.localScale.Equals(scale); }
        internal void Apply(Transform target, JObject condition, TransformConfig config)
        {
            Restore(target);
            if (condition.Value<bool>("captured_pose")) return;
            target.localRotation = Quaternion.Euler(Vector(condition["rotation_euler_local_delta"])) * rotation;
            JToken desired = condition["world_units_per_ground_metre_up"];
            if (desired != null && desired.Type != JTokenType.Null)
            {
                float current = MetricAxes(target, config)[2];
                if (!(current > 0f) || float.IsInfinity(current)) throw new InvalidDataException("Degenerate map scale.");
                target.localScale = scale * (desired.Value<float>() / current);
            }
            JToken translation = condition["position_world"];
            if (translation != null && translation.Type != JTokenType.Null) target.position = Vector(translation);
        }
    }

    private static Vector3 Vector(JToken token) { return new Vector3(token[0].Value<float>(), token[1].Value<float>(), token[2].Value<float>()); }
    private static float[] MetricAxes(Transform map, TransformConfig config)
    {
        float[] r = config.colmap_to_enu.R_rowmajor; float s = config.colmap_to_enu.scale;
        return new[] { map.TransformVector(new Vector3(r[0], r[1], r[2]) / s).magnitude,
            map.TransformVector(new Vector3(r[3], r[4], r[5]) / s).magnitude,
            map.TransformVector(new Vector3(r[6], r[7], r[8]) / s).magnitude };
    }
    private static JObject MatrixRecord(Transform map, JObject condition, TransformConfig config, string phase)
    {
        var matrix = new JArray(); for (int i = 0; i < 16; i++) matrix.Add((double)map.localToWorldMatrix[i]);
        return new JObject { ["phase"] = phase, ["condition"] = condition.DeepClone(), ["local_to_world_column_major"] = matrix,
            ["world_units_per_ground_metre_enu_axes"] = new JArray(MetricAxes(map, config).Select(x => (double)x)) };
    }
    private static JArray Hierarchy(Transform map)
    {
        var result = new JArray();
        for (Transform t = map; t != null; t = t.parent)
            result.Add(new JObject { ["path"] = HierarchyPath(t), ["position"] = new JArray(t.localPosition.x, t.localPosition.y, t.localPosition.z),
                ["rotation_xyzw"] = new JArray(t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w),
                ["scale"] = new JArray(t.localScale.x, t.localScale.y, t.localScale.z) });
        return result;
    }
    private static string HierarchyPath(Transform target) { return target.parent == null ? target.name : HierarchyPath(target.parent) + "/" + target.name; }
    private static bool SameConfig(TransformConfig a, TransformConfig b)
    {
        return a != null && b != null && a.origin_wgs84.lat == b.origin_wgs84.lat && a.origin_wgs84.lon == b.origin_wgs84.lon &&
            a.origin_wgs84.alt == b.origin_wgs84.alt && a.colmap_to_enu.scale == b.colmap_to_enu.scale &&
            a.colmap_to_enu.R_rowmajor.SequenceEqual(b.colmap_to_enu.R_rowmajor) && a.colmap_to_enu.t.SequenceEqual(b.colmap_to_enu.t);
    }
    private static void Progress(string detail, float fraction)
    { if (EditorUtility.DisplayCancelableProgressBar("E2 scene consistency (Play mode stays paused)", detail, fraction)) throw new OperationCanceledException("Experiment cancelled. Partial capture retained."); }
    private static void WriteJson(string path, JToken value) { File.WriteAllText(path, value.ToString(Formatting.Indented)); }
    private static string Hash(string path)
    { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
    private static string SourceHash(string path)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() + "\n"))).Replace("-", "").ToLowerInvariant(); }
}
