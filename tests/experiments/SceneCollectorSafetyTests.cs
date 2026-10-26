using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

// Verifies guards and pose bookkeeping only. Native matrices/save execution require Unity.
public static class SceneCollectorSafetyTests
{
    public static int Main()
    {
        int passed = 0;
        foreach (bool playing in new[] { false, true })
        {
            EditorApplication.isPlaying = playing; EditorApplication.isPaused = false;
            try { SceneConsistencyCollector.Run("missing-folder", null, 100); throw new Exception("Unpaused run was allowed."); }
            catch (InvalidOperationException) { passed++; }
        }
        EditorApplication.isPlaying = EditorApplication.isPaused = true;
        try { SceneConsistencyCollector.Run("missing-folder", null, 100); throw new Exception("Null source was allowed."); }
        catch (ArgumentException) { passed++; }
        Type type = typeof(SceneConsistencyCollector).GetNestedType("SavedPose", BindingFlags.NonPublic);
        var go = new GameObject("pose safety double");
        go.transform.localPosition = new Vector3(3, 4, 5);
        go.transform.localScale = new Vector3(-2, 2, 2);
        go.transform.localRotation = new Quaternion { x=1, y=2, z=3, w=4 };
        object saved = Activator.CreateInstance(type, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic, null, new object[] { go.transform }, null);
        go.transform.localPosition = new Vector3(10000, 10000, -10000);
        go.transform.localScale = new Vector3(100, 200, 300);
        type.GetMethod("Restore", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(saved, new object[] { go.transform });
        if (!(bool)type.GetMethod("Matches", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(saved, new object[] { go.transform })) throw new Exception("Pose was not restored exactly.");
        if (go.transform.localScale.x != -2) throw new Exception("Reflection was lost on restore.");
        passed++;
        var config = new TransformConfig { origin_wgs84 = new OriginWgs84 {lat=37,lon=23,alt=100},
            colmap_to_enu = new ColmapToEnu {scale=2,R_rowmajor=new[] {1f,0,0,0,1,0,0,0,1},t=new[] {0f,0,0}} };
        JObject pose = JObject.Parse("{captured_pose:false,position_world:[2,3,4],rotation_euler_local_delta:[10,20,30],world_units_per_ground_metre_up:0.001}");
        type.GetMethod("Apply", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(saved, new object[] { go.transform, pose, config });
        if (go.transform.localScale.x >= 0 || Math.Abs(go.transform.localScale.y-.002f) > 1e-8 || go.transform.localPosition.x != 2)
            throw new Exception("Scale target/translation/reflection bookkeeping failed.");
        type.GetMethod("Restore", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(saved, new object[] { go.transform });
        if (!(bool)type.GetMethod("Matches", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(saved, new object[] { go.transform })) throw new Exception("Transformed pose did not restore.");
        passed++;
        Console.WriteLine("Scene collector compilation and safety bookkeeping: " + passed + " passed (engine doubles; no native measurement).");
        return 0;
    }
}
