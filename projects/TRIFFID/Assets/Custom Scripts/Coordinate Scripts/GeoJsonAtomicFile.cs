using System;
using System.IO;

public static class GeoJsonAtomicFile
{
    // Never fall back to truncating the only saved copy. If replacement fails,
    // the caller retains dirty state and reports the failed save to the user.
    public static void Write(string path, string json)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Save path is empty.");
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            { writer.Write(json); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally
        { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class GeoJsonSaveSequence
{
    public readonly object SyncRoot = new object();
    private int lastWrittenVersion = -1;

    public bool Write(string path, string json, int version)
    {
        lock (SyncRoot)
        {
            if (version < lastWrittenVersion) return false;
            if (File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            GeoJsonAtomicFile.Write(path, json);
            lastWrittenVersion = version;
            return true;
        }
    }
}
