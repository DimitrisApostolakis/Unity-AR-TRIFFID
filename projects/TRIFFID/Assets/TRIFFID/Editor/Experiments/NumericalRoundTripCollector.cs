using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;

// Measurement only: invokes the unchanged production conversion in both directions.
public static class NumericalRoundTripCollector
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public const string InputHeader = "id,group,cx,cy,cz,lon,lat,height";
    public const string OutputHeader = "id,group,c_start_x,c_start_y,c_start_z,geo_c_x,geo_c_y,geo_c_z,geo_back_lon,geo_back_lat,geo_back_h,c_geo_lon,c_geo_lat,c_geo_h,c_back_x,c_back_y,c_back_z";

    public static int Run(string directory, string profile, string geoUtilsPath)
    {
        string alignment = Path.Combine(directory, "alignment.json");
        string samples = Path.Combine(directory, "samples.csv");
        string output = Path.Combine(directory, "raw.csv");
        string metadata = output + ".runtime.json";
        if (File.Exists(output) || File.Exists(metadata))
            throw new IOException("This folder already contains raw measurements. Prepare a new run folder.");
        TransformConfig config = JsonConvert.DeserializeObject<TransformConfig>(File.ReadAllText(alignment));
        Validate(config);
        int count = 0;
        using (var input = new StreamReader(samples))
        using (var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write)))
        {
            if (input.ReadLine() != InputHeader) throw new InvalidDataException("Unexpected sample CSV header.");
            writer.WriteLine(OutputHeader);
            string line;
            while ((line = input.ReadLine()) != null)
            {
                string[] columns = line.Split(',');
                if (columns.Length != 8) throw new InvalidDataException("Invalid sample row.");
                double[] v = new double[6];
                for (int i = 0; i < v.Length; i++)
                {
                    v[i] = double.Parse(columns[i + 2], Invariant);
                    if (!Finite(v[i])) throw new InvalidDataException("Nonfinite input sample.");
                }
                var start = new Vector3((float)v[0], (float)v[1], (float)v[2]);
                if (!Finite(start.x) || !Finite(start.y) || !Finite(start.z) ||
                    v[3] < -180 || v[3] > 180 || v[4] <= -90 || v[4] >= 90)
                    throw new InvalidDataException("Input outside supported finite geodetic/float domain.");
                Vector3 geoC = GeoUtils.GetColmapPosition(v[3], v[4], v[5], config);
                double geoLon, geoLat, geoHeight, cLon, cLat, cHeight;
                GeoUtils.ColmapToWgs84(geoC, config, out geoLon, out geoLat, out geoHeight);
                GeoUtils.ColmapToWgs84(start, config, out cLon, out cLat, out cHeight);
                Vector3 back = GeoUtils.GetColmapPosition(cLon, cLat, cHeight, config);
                double[] values = { start.x, start.y, start.z, geoC.x, geoC.y, geoC.z,
                    geoLon, geoLat, geoHeight, cLon, cLat, cHeight, back.x, back.y, back.z };
                writer.Write(columns[0] + "," + columns[1]);
                foreach (double value in values) writer.Write("," + value.ToString("R", Invariant));
                writer.WriteLine();
                count++;
            }
        }
        var runtime = new
        {
            collector_version = 1, profile = profile, sample_count = count,
            framework = Environment.Version.ToString(), process_bitness = IntPtr.Size * 8,
            alignment_sha256 = Hash(alignment), samples_sha256 = Hash(samples), raw_sha256 = Hash(output),
            geo_utils_source_sha256 = Hash(geoUtilsPath), geo_utils_lf_sha256 = SourceHash(geoUtilsPath), calibration_float = config
        };
        File.WriteAllText(metadata, JsonConvert.SerializeObject(runtime, Formatting.Indented));
        return count;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void Validate(TransformConfig config)
    {
        if (config == null || config.origin_wgs84 == null || config.colmap_to_enu == null)
            throw new InvalidDataException("Missing calibration.");
        OriginWgs84 o = config.origin_wgs84;
        ColmapToEnu c = config.colmap_to_enu;
        if (!Finite(o.lon) || !Finite(o.lat) || !Finite(o.alt) || o.lon < -180 || o.lon > 180 || o.lat <= -90 || o.lat >= 90 ||
            !Finite(c.scale) || c.scale <= 0 || c.R_rowmajor == null || c.R_rowmajor.Length != 9 || c.t == null || c.t.Length != 3)
            throw new InvalidDataException("Invalid calibration origin/scale/shape.");
        foreach (float value in c.R_rowmajor) if (!Finite(value)) throw new InvalidDataException("Nonfinite rotation.");
        foreach (float value in c.t) if (!Finite(value)) throw new InvalidDataException("Nonfinite translation.");
        float[] r = c.R_rowmajor;
        double determinant = (double)r[0] * (r[4] * (double)r[8] - r[5] * (double)r[7]) -
            r[1] * (r[3] * (double)r[8] - r[5] * (double)r[6]) + r[2] * (r[3] * (double)r[7] - r[4] * (double)r[6]);
        if (Math.Abs(determinant - 1) > 1e-5) throw new InvalidDataException("Rotation is not proper.");
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
        {
            double dot = 0;
            for (int row = 0; row < 3; row++) dot += r[row * 3 + a] * (double)r[row * 3 + b];
            if (Math.Abs(dot - (a == b ? 1 : 0)) > 1e-5) throw new InvalidDataException("Rotation is not orthonormal.");
        }
    }

    private static string SourceHash(string path)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() + "\n");
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(text)).Replace("-", "").ToLowerInvariant();
    }

    private static string Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var file = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Experiments/Numerical Round Trip/Run Prepared Folder")]
    private static void RunPreparedFolder()
    {
        string directory = UnityEditor.EditorUtility.OpenFolderPanel("Select the prepared numerical experiment folder", "", "");
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            int count = Run(directory, "Unity Editor " + Application.unityVersion, Path.Combine(Application.dataPath, "TRIFFID/Runtime/Geospatial/Coordinates/GeoUtils.cs"));
            Debug.Log("[NumericalRoundTrip] Collected " + count + " samples using native Unity vectors. Analyze with: python LocalScripts/run_numerical_roundtrip.py --analyze-run \"" + directory + "\"");
        }
        catch (Exception error) { Debug.LogException(error); }
    }
#endif
}
