using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;

// Implementation of the existing JsonSpawner component. No additional scene component.
public partial class JsonSpawner
{
    private Vector3 GetColmapPosition(double lon, double lat, double alt)
    {
        EnsureTransformDataLoaded();
        return GeoUtils.GetColmapPosition(lon, lat, alt, transformData);
    }

    public Vector3Double ColmapToWgs84(Vector3 colmapPos)
    {
        EnsureTransformDataLoaded();
        if (transformData == null || transformData.colmap_to_enu == null || transformData.origin_wgs84 == null)
        {
            if (!transformLoadWarningShown)
            {
                Debug.LogWarning("[JsonSpawner] ColmapToWgs84: transformData not loaded yet.");
                transformLoadWarningShown = true;
            }
            return new Vector3Double();
        }

        GeoUtils.ColmapToWgs84(colmapPos, transformData, out double lon, out double lat, out double alt);
        return new Vector3Double { lon = lon, lat = lat, alt = alt };
    }

    private void EnsureTransformDataLoaded()
    {
        if (transformData != null && transformData.colmap_to_enu != null && transformData.origin_wgs84 != null)
            return;

        string resolvedPath = ResolveTransformJsonPathForRuntime();
        bool shouldLogValidation = !transformLoadWarningShown;
        if (!ValidateTransformConfigFile(resolvedPath, shouldLogValidation))
        {
            transformLoadWarningShown = true;
            return;
        }

        try
        {
            TransformConfig loadedTransform = JsonConvert.DeserializeObject<TransformConfig>(File.ReadAllText(resolvedPath));
            if (!TryValidateTransformConfig(loadedTransform, out string transformError))
            {
                if (!transformLoadWarningShown)
                    Debug.LogError($"[JsonSpawner] Invalid transform config: {transformError}", this);

                transformLoadWarningShown = true;
                return;
            }

            transformData = loadedTransform;
            transformLoadWarningShown = false;
        }
        catch (Exception ex)
        {
            if (!transformLoadWarningShown)
            {
                Debug.LogWarning($"[JsonSpawner] Failed loading transform config: {ex.Message}");
                transformLoadWarningShown = true;
            }
        }
    }

    private double GetTransformOriginAltitude()
    {
        EnsureTransformDataLoaded();

        if (transformData?.origin_wgs84 == null)
            return 0d;

        return transformData.origin_wgs84.alt;
    }

    private string ResolveTransformJsonPathForRuntime()
    {
        if (useSharedTransformPathJson)
        {
            if (!StreamingAssetsPathResolver.TryResolvePathFromStreamingAssetsJson(
                    sharedTransformPathsJsonFile,
                    transformPathJsonKey,
                    out string resolvedPath,
                    out string error))
            {
                Debug.LogError("[JsonSpawner] Failed to resolve shared transform path: " + error, this);
                return string.Empty;
            }

            return resolvedPath;
        }

        return ResolveJsonPathForRuntime(transformJsonPath);
    }

    [ContextMenu("Validate Shared Transform Path")]
    public void ValidateSharedTransformPath()
    {
        string resolvedTransformPath = ResolveTransformJsonPathForRuntime();

        if (string.IsNullOrWhiteSpace(resolvedTransformPath))
        {
            Debug.LogError("[JsonSpawner] Resolved transform path is empty.", this);
            return;
        }

        bool exists = File.Exists(resolvedTransformPath);

        Debug.Log(
            "[JsonSpawner] Shared Transform Path Validation\n" +
            $"Use Shared Transform Path Json: {useSharedTransformPathJson}\n" +
            $"Shared Config File: {sharedTransformPathsJsonFile}\n" +
            $"Transform Path Json Key: {transformPathJsonKey}\n" +
            $"Resolved Transform Path: {resolvedTransformPath}\n" +
            $"Transform File Exists: {exists}",
            this
        );
    }

    private static string ResolveJsonPathForRuntime(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string normalized = rawPath.Replace("/", Path.DirectorySeparatorChar.ToString());

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
            return normalized;

        return Path.Combine(projectRoot, normalized);
    }

    private static string ResolveSavePathForRuntime(string rawPath, bool useProjectFolder)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            rawPath = DefaultRelativeSavePath;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string normalized = rawPath.Replace("\\", "/").TrimStart('/');
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring("Assets/".Length);

        string basePath = Application.persistentDataPath;
        if (useProjectFolder)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(projectRoot))
                basePath = projectRoot;
        }

        string combined = Path.Combine(basePath, normalized.Replace("/", Path.DirectorySeparatorChar.ToString()));
        return combined;
    }

    private static void ValidateSavePathConfiguration(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogError("[JsonSpawner] GeoJSON save path is empty.");
            return;
        }

        if (Directory.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] GeoJSON save path points to a directory: {path}");
            return;
        }

        ValidateGeoJsonFileType(path, true, "output GeoJSON");
    }

    private static bool ValidateGeoJsonInputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogError("[JsonSpawner] GeoJSON input path is empty.");
            return false;
        }

        if (Directory.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] GeoJSON input path points to a directory: {path}");
            return false;
        }

        if (!ValidateGeoJsonFileType(path, true))
            return false;

        if (!File.Exists(path))
        {
            Debug.LogError($"[JsonSpawner] File not found: {path}");
            return false;
        }

        return true;
    }

    private static bool ValidateTransformConfigFile(string path, bool logErrors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (logErrors)
                Debug.LogError("[JsonSpawner] Transform config path is empty.");
            return false;
        }

        if (Directory.Exists(path))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] Transform config path points to a directory: {path}");
            return false;
        }

        string extension = GetPathExtension(path);
        if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] Invalid transform config file type. Expected .json, got {FormatExtension(extension)}: {path}");
            return false;
        }

        if (!File.Exists(path))
        {
            if (logErrors)
                Debug.LogError($"[JsonSpawner] File not found: {path}");
            return false;
        }

        return true;
    }

    private static bool ValidateGeoJsonFileType(string path, bool logErrors, string description = "GeoJSON")
    {
        string extension = GetPathExtension(path);
        bool valid = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(extension, ".geojson", StringComparison.OrdinalIgnoreCase);
        if (!valid && logErrors)
            Debug.LogError($"[JsonSpawner] Invalid {description} file type. Expected .json or .geojson, got {FormatExtension(extension)}: {path}");

        return valid;
    }

    private static string GetPathExtension(string path)
    {
        try
        {
            return Path.GetExtension(path) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string FormatExtension(string extension)
    {
        return string.IsNullOrEmpty(extension) ? "<none>" : extension;
    }

    private static bool TryValidateTransformConfig(TransformConfig config, out string error)
    {
        if (config == null)
        {
            error = "document is null.";
            return false;
        }

        if (config.origin_wgs84 == null)
        {
            error = "origin_wgs84 is missing.";
            return false;
        }

        if (!IsFiniteNumber(config.origin_wgs84.lat) ||
            !IsFiniteNumber(config.origin_wgs84.lon) ||
            !IsFiniteNumber(config.origin_wgs84.alt))
        {
            error = "origin_wgs84 latitude, longitude, and altitude must be finite.";
            return false;
        }

        if (config.colmap_to_enu == null)
        {
            error = "colmap_to_enu is missing.";
            return false;
        }

        ColmapToEnu transform = config.colmap_to_enu;
        if (!IsFiniteNumber(transform.scale) || transform.scale == 0f)
        {
            error = "scale must be finite and non-zero.";
            return false;
        }

        if (transform.R_rowmajor == null || transform.R_rowmajor.Length < 9)
        {
            error = "R_rowmajor must contain at least 9 finite values.";
            return false;
        }

        for (int i = 0; i < 9; i++)
        {
            if (!IsFiniteNumber(transform.R_rowmajor[i]))
            {
                error = "R_rowmajor must contain at least 9 finite values.";
                return false;
            }
        }

        if (transform.t == null || transform.t.Length < 3)
        {
            error = "t must contain at least 3 finite values.";
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (!IsFiniteNumber(transform.t[i]))
            {
                error = "t must contain at least 3 finite values.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string SanitizeSaveFilePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return DefaultRelativeSavePath;

        string normalized = rawPath.Replace("\\", "/").Trim();

        if (normalized.EndsWith(LegacyCoordinatesChangesSuffix, StringComparison.OrdinalIgnoreCase))
            return DefaultRelativeSavePath;

        return normalized;
    }

    private static string SanitizeGeoJsonPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return DefaultGeoJsonPath;

        string normalized = rawPath.Replace("\\", "/").Trim();

        if (normalized.EndsWith(LegacyCoordinatesChangesSuffix, StringComparison.OrdinalIgnoreCase))
            return DefaultGeoJsonPath;

        if (normalized.StartsWith("Coordinates/", StringComparison.OrdinalIgnoreCase))
            return "Assets/" + normalized;

        return normalized;
    }
}
