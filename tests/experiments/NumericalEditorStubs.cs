// Compiles the native Editor menu branch; this is not native Unity execution.
using System;
namespace UnityEngine
{
    public static class Application
    {
        public static string unityVersion = "Editor double";
        public static string dataPath = ".";
    }
    public static class Debug
    {
        public static void Log(object value) { }
        public static void LogException(Exception error) { }
    }
}
namespace UnityEditor
{
    public sealed class MenuItem : Attribute { public MenuItem(string path) { } }
    public static class EditorUtility
    {
        public static string OpenFolderPanel(string title, string folder, string name) { return ""; }
    }
}
