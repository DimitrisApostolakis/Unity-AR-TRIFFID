using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Compile/guard tests plus real production polygon-validator cases. These do
// not execute Unity physics, matrices, rendering or collect paper measurements.
public static class AppNumericsSafetyTests
{
    public static int Main()
    {
        int passed=0;
        foreach(bool playing in new[]{false,true})
        {
            EditorApplication.isPlaying=playing;EditorApplication.isPaused=false;
            try { AppNumericsCollector.Run("unused",null);throw new Exception("Unpaused numerical run accepted."); }
            catch(InvalidOperationException){passed++;}
        }
        MethodInfo menu=typeof(AppPerformanceCollector).GetMethod("StartMenu",BindingFlags.Static|BindingFlags.NonPublic);
        EditorApplication.isPlaying=true;EditorApplication.isPaused=true;Debug.LastException=null;menu.Invoke(null,null);
        if(!(Debug.LastException is InvalidOperationException)||!Debug.LastException.Message.Contains("UNPAUSED"))throw new Exception("Paused performance run accepted.");
        passed++;
        MethodInfo polygons=typeof(AppNumericsCollector).GetMethod("Polygons",BindingFlags.Static|BindingFlags.NonPublic);
        JArray cases=(JArray)polygons.Invoke(null,null);
        if(cases.Count!=11)throw new Exception("Missing fixture coverage.");
        foreach(JObject test in cases)
        {
            if(!test.Value<bool>("passed"))throw new Exception("Polygon fixture failed: "+test.Value<string>("id"));
            passed++;
        }
        Console.WriteLine("App numerical collector compilation/guards and production polygon fixtures: "+passed+" passed; no native physics/rendering measurement.");
        return 0;
    }
}
