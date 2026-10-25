// GeoUtils only stores/accesses Vector3 fields: no engine transforms or physics are emulated.
using System;
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 zero => new Vector3(0,0,0);
    }
    public class GameObject { } // Unused PrefabEntry model dependency.
}
public static class NumericalHeadless
{
    public static int Main(string[] arguments)
    {
        try { NumericalRoundTripCollector.Run(arguments[0], "headless Mono; float Vector3 carrier; no Unity engine", arguments[1]); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
