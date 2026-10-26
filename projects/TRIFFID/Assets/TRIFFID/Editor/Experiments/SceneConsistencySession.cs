using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Editor-only adapter: existing component methods run in an inactive, isolated fixture.
// No networking component or application save path is copied into the fixture.
internal sealed class SceneConsistencySession : IDisposable
{
    internal sealed class Node
    {
        public Transform transform;
        public Feature feature;
        public JArray coordinate;
        public string path;
        public string Key => feature.id + "/" + path;
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly GameObject sandbox;
    internal readonly JsonSpawner Spawner;
    internal Transform Map => sandbox.transform;
    internal Root Data => (Root)Get(Spawner, "currentRootData");
    internal string SavePath => Spawner.saveFilePath;

    internal SceneConsistencySession(Transform parent, string alignment, string fixture, string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        sandbox = new GameObject("E2 temporary inactive fixture");
        sandbox.hideFlags = HideFlags.HideAndDontSave;
        sandbox.SetActive(false);
        sandbox.transform.SetParent(parent, false);
        try
        {
            GameObject prefab = Child("E2 temporary node prefab");
            prefab.SetActive(false);
            prefab.AddComponent<PointData>();
            FloatingIcon icon = prefab.AddComponent<FloatingIcon>();
            icon.floatAmplitude = 0f;
            GameObject host = Child("E2 temporary persistence component");
            Spawner = host.AddComponent<JsonSpawner>();
            Spawner.mapTransform = Map;
            Spawner.transformJsonPath = alignment;
            Spawner.geoJsonPath = fixture;
            Spawner.saveFilePath = Path.Combine(stateDirectory, "state.json");
            Spawner.nodePrefab = prefab;
            Spawner.prefabEntries = new List<PrefabEntry>
            {
                new PrefabEntry { name = "animal", prefab = prefab },
                new PrefabEntry { name = "road", prefab = prefab },
                new PrefabEntry { name = "safe", prefab = prefab }
            };
            Set(Spawner, "useSharedTransformPathJson", false);
            Set(Spawner, "enableAutoSaveTimer", false);
            Set(Spawner, "enableSurfaceSnapFallback", false);
            // A configured target with no collider prevents fallback to unrelated scene physics.
            Set(Spawner, "surfaceSnapTargetObject", Child("E2 no surface query target"));
            Set(Spawner, "autoActivateSurfaceForSnap", false);
            Invoke(Spawner, "Awake");
            Reload();
        }
        catch { UnityEngine.Object.DestroyImmediate(sandbox); throw; }
    }

    private GameObject Child(string name)
    {
        var child = new GameObject(name);
        child.hideFlags = HideFlags.HideAndDontSave;
        child.transform.SetParent(Map, false);
        return child;
    }

    internal void Reload()
    {
        DestroyGenerated();
        Spawner.SpawnFromJson();
        if (Data == null || Data.features == null || Data.features.Count == 0)
            throw new InvalidDataException("The fixture did not load through JsonSpawner.SpawnFromJson.");
    }

    internal Root Save()
    {
        Invoke(Spawner, "MarkDataDirty");
        Invoke(Spawner, "FlushPendingSaveSynchronously");
        if ((int)Get(Spawner, "dataVersion") != (int)Get(Spawner, "lastSavedVersion") || !File.Exists(SavePath))
            throw new IOException("The fixture save was rejected or did not finish.");
        return JsonConvert.DeserializeObject<Root>(File.ReadAllText(SavePath));
    }

    internal void AcknowledgeFixture()
    {
        // Explicit synthetic acknowledged baseline, only inside this disconnected test copy.
        Data.syncState = new GeoJsonSyncState();
        foreach (Feature feature in Data.features)
        {
            Data.syncState.remoteIds[feature.id] = feature.id;
            Data.syncState.acknowledgedHashes[feature.id] = GeoJsonOutbox.Fingerprint(feature);
        }
        GeoJsonAtomicFile.Write(SavePath, JsonConvert.SerializeObject(Data, Formatting.Indented));
    }

    internal List<Node> Nodes()
    {
        var coordinates = new List<Node>();
        foreach (Feature feature in Data.features)
            Flatten(feature.geometry.coordinates, "", feature, coordinates);
        var result = new List<Node>();
        foreach (object mapping in (IEnumerable)Get(Spawner, "nodeMappings"))
        {
            Type type = mapping.GetType();
            if ((bool)type.GetField("isCentroid").GetValue(mapping)) continue;
            var node = (Transform)type.GetField("node").GetValue(mapping);
            var feature = (Feature)type.GetField("parentFeature").GetValue(mapping);
            var coordinate = (JArray)type.GetField("coordArray").GetValue(mapping);
            Node binding = coordinates.SingleOrDefault(x => ReferenceEquals(x.feature, feature) && ReferenceEquals(x.coordinate, coordinate));
            if (node == null || binding == null) throw new InvalidDataException("A spawned vertex has no coordinate binding.");
            result.Add(new Node { transform = node, feature = feature, coordinate = coordinate, path = binding.path });
        }
        if (result.Count != coordinates.Count || result.Select(x => x.Key).Distinct().Count() != coordinates.Count)
            throw new InvalidDataException("Missing, skipped or duplicated fixture vertices.");
        return result;
    }

    private static void Flatten(JToken token, string path, Feature feature, List<Node> result)
    {
        JArray array = token as JArray;
        if (array == null || array.Count == 0) throw new InvalidDataException("Malformed fixture coordinates.");
        if (!(array[0] is JArray))
        {
            if (array.Count != 3) throw new InvalidDataException("E2 requires explicit longitude/latitude/height.");
            result.Add(new Node { feature = feature, coordinate = array, path = path });
            return;
        }
        for (int i = 0; i < array.Count; i++)
            Flatten(array[i], path.Length == 0 ? i.ToString() : path + "/" + i, feature, result);
    }

    internal Vector3 Local(Node node)
    {
        return (Vector3)typeof(JsonSpawner).GetMethod("GetStableMapLocalPosition", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { node.transform, Map });
    }

    internal double[] Geographic(Node node)
    {
        JsonSpawner.Vector3Double p = Spawner.ColmapToWgs84(Local(node));
        return new[] { p.lon, p.lat, p.alt };
    }

    private void DestroyGenerated()
    {
        // Production destruction is deferred in Play mode. These are owned experiment objects,
        // so release them immediately to keep 1000 synchronous reloads bounded in memory.
        IList generated = Spawner == null ? null : Get(Spawner, "spawnedObjects") as IList;
        if (generated == null) return;
        foreach (GameObject go in generated)
        {
            if (go == null) continue;
            foreach (LineRenderer line in go.GetComponentsInChildren<LineRenderer>(true))
                if (line.sharedMaterial != null) UnityEngine.Object.DestroyImmediate(line.sharedMaterial);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    public void Dispose()
    {
        try { DestroyGenerated(); }
        finally { if (sandbox != null) UnityEngine.Object.DestroyImmediate(sandbox); }
    }

    internal static object Get(object target, string name)
    {
        FieldInfo field = typeof(JsonSpawner).GetField(name, Private);
        if (field == null) throw new MissingFieldException(typeof(JsonSpawner).FullName, name);
        return field.GetValue(target);
    }

    internal static void Set(object target, string name, object value)
    {
        FieldInfo field = typeof(JsonSpawner).GetField(name, Private);
        if (field == null) throw new MissingFieldException(typeof(JsonSpawner).FullName, name);
        field.SetValue(target, value);
    }

    internal static void Invoke(object target, string name)
    {
        MethodInfo method = typeof(JsonSpawner).GetMethod(name, Private);
        if (method == null) throw new MissingMethodException(typeof(JsonSpawner).FullName, name);
        try { method.Invoke(target, null); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
