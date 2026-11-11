using System;
using System.Collections;
using System.Globalization;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class ColmapGaussianRotationAligner : MonoBehaviour
{
    public enum EnuToUnityAxisMode
    {
        XEast_YUp_ZNorth,
        XEast_YNorth_ZUp,
        Custom
    }

    [Header("References")]
    public Transform gaussianRenderer;

    [Tooltip("Optional collider follower. Its Gaussian Renderer reference must be this same renderer.")]
    public MapColliderSync mapColliderSync;

    [Header("Transform Path Loading")]
    public string transformJsonPath = "colmap_to_unity_transform.json";
    public ProjectPathResolver.PathRoot inputPathRoot = ProjectPathResolver.PathRoot.StreamingAssets;
    public bool useSharedTransformPathJson = true;
    public string sharedTransformPathsJsonFile = "project_paths.json";
    public string transformPathJsonKey = "colmap_aligner_transform_path";

    [Header("Apply Behaviour")]
    public bool applyOnStart = true;
    public bool applyOnEnable = false;
    public bool applyAfterOneFrame = true;
    public bool rememberInitialRotation = true;
    public bool syncColliderAfterApply = true;

    [Header("Coordinate Mapping")]
    [Tooltip("Default: Unity = (East, Up, North).")]
    public EnuToUnityAxisMode enuToUnityAxisMode = EnuToUnityAxisMode.XEast_YUp_ZNorth;

    [Tooltip("For Custom mode only. This is the Unity direction corresponding to ENU East.")]
    public Vector3 customEastAxis = Vector3.right;

    [Tooltip("For Custom mode only. This is the Unity direction corresponding to ENU North.")]
    public Vector3 customNorthAxis = Vector3.forward;

    [Tooltip("For Custom mode only. This is the Unity direction corresponding to ENU Up.")]
    public Vector3 customUpAxis = Vector3.up;

    [Tooltip("Use this if the JSON rotation appears to be stored with the opposite row/column convention.")]
    public bool transposeRotationMatrix = false;

    [Tooltip("Use this if the reconstruction rotates in the opposite direction.")]
    public bool invertRotation = false;

    [Tooltip("Flip final Unity X after ENU-to-Unity conversion.")]
    public bool flipX = false;

    [Tooltip("Flip final Unity Y after ENU-to-Unity conversion.")]
    public bool flipY = false;

    [Tooltip("Flip final Unity Z after ENU-to-Unity conversion.")]
    public bool flipZ = false;

    [Header("Debug")]
    public bool logDetails = true;

    private const float Epsilon = 1e-6f;

    private bool initialCaptured;
    private Quaternion initialLocalRotation = Quaternion.identity;

    private struct LoadedRotation
    {
        public string ResolvedPath;
        public double[] RotationRowMajor;
        public bool HasScale;
        public double Scale;
    }

    private void Awake()
    {
        CaptureInitialRotationIfNeeded();
    }

    private void OnEnable()
    {
        CaptureInitialRotationIfNeeded();

        if (applyOnEnable)
        {
            ApplyFromLifecycle();
        }
    }

    private void Start()
    {
        CaptureInitialRotationIfNeeded();

        if (applyOnStart)
        {
            ApplyFromLifecycle();
        }
    }

    private void ApplyFromLifecycle()
    {
        if (applyAfterOneFrame && Application.isPlaying)
        {
            StartCoroutine(ApplyAfterOneFrameCoroutine());
        }
        else
        {
            ApplyJsonRotationAndSyncCollider();
        }
    }

    private IEnumerator ApplyAfterOneFrameCoroutine()
    {
        yield return null;
        ApplyJsonRotationAndSyncCollider();
    }

    [ContextMenu("Validate Transform JSON")]
    public void ValidateTransformJson()
    {
        if (TryLoadRotationJson(out LoadedRotation loaded))
        {
            TriffidLog.Info(
                "ColmapGaussianRotationAligner",
                "Transform JSON is valid." +
                TriffidLog.Fields(
                    ("Path", loaded.ResolvedPath),
                    ("Scale", loaded.HasScale ? loaded.Scale.ToString("G9", CultureInfo.InvariantCulture) : "not used"),
                    ("Rotation (row-major)", FormatRowMajor3x3(loaded.RotationRowMajor))),
                this);
        }
    }

    [ContextMenu("Validate Shared Transform Path")]
    public void ValidateSharedTransformPath()
    {
        if (!TryResolveTransformJsonPath(out string resolvedTransformPath))
        {
            return;
        }

        bool exists = File.Exists(resolvedTransformPath);

        string report = TriffidLog.Fields(
            ("Use shared path JSON", useSharedTransformPathJson),
            ("Shared config file", sharedTransformPathsJsonFile),
            ("Path key", transformPathJsonKey),
            ("Resolved path", resolvedTransformPath),
            ("File exists", exists));
        if (exists) TriffidLog.Info("ColmapGaussianRotationAligner", "Shared transform path is valid." + report, this);
        else TriffidLog.Warning("ColmapGaussianRotationAligner", "Shared transform path resolved, but the file does not exist." + report, this);
    }

    [ContextMenu("Apply JSON Rotation")]
    public void ApplyJsonRotation()
    {
        ApplyJsonRotationInternal(false);
    }

    [ContextMenu("Apply JSON Rotation And Sync Collider")]
    public void ApplyJsonRotationAndSyncCollider()
    {
        ApplyJsonRotationInternal(syncColliderAfterApply);
    }

    [ContextMenu("Sync Collider Now")]
    public void SyncColliderNow()
    {
        if (mapColliderSync == null)
        {
            TriffidLog.Warning("ColmapGaussianRotationAligner", "MapColliderSync is not assigned. Collider sync skipped.", this);
            return;
        }

        if (mapColliderSync.GaussianRenderer != gaussianRenderer)
        {
            TriffidLog.Warning("ColmapGaussianRotationAligner", "Collider follower must reference the same Gaussian Renderer. Sync skipped.", this);
            return;
        }
        mapColliderSync.SyncToMap();
    }

    [ContextMenu("Reset Gaussian Rotation Only")]
    public void ResetGaussianRotationOnly()
    {
        if (gaussianRenderer == null)
        {
            TriffidLog.Warning("ColmapGaussianRotationAligner", "Gaussian Renderer is not assigned. Nothing was reset.", this);
            return;
        }

        gaussianRenderer.localRotation = rememberInitialRotation && initialCaptured
            ? initialLocalRotation
            : Quaternion.identity;

        if (syncColliderAfterApply)
        {
            SyncColliderNow();
        }

        TriffidLog.Info("ColmapGaussianRotationAligner", "Reset the Gaussian Renderer rotation (position and scale unchanged).", this);
    }

    private void ApplyJsonRotationInternal(bool shouldSyncCollider)
    {
        if (gaussianRenderer == null)
        {
            TriffidLog.Warning("ColmapGaussianRotationAligner", "Gaussian Renderer is not assigned. Nothing was applied.", this);
            return;
        }

        CaptureInitialRotationIfNeeded();

        if (!TryLoadRotationJson(out LoadedRotation loaded))
        {
            return;
        }

        if (!TryBuildUnityRotation(loaded, out Quaternion finalRotation))
        {
            return;
        }

        if (!IsFinite(finalRotation))
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Final rotation contains NaN or Infinity. Nothing was applied.", this);
            return;
        }

        gaussianRenderer.localRotation = finalRotation;

        if (logDetails)
        {
            TriffidLog.Info(
                "ColmapGaussianRotationAligner",
                "Applied JSON rotation to the Gaussian Renderer (position and scale unchanged)." +
                TriffidLog.Fields(
                    ("JSON path", loaded.ResolvedPath),
                    ("Gaussian Renderer", gaussianRenderer.name),
                    ("Local rotation (euler)", finalRotation.eulerAngles),
                    ("Transpose matrix", transposeRotationMatrix),
                    ("Invert rotation", invertRotation),
                    ("ENU-to-Unity axes", enuToUnityAxisMode),
                    ("Flip X/Y/Z", $"{flipX}/{flipY}/{flipZ}")),
                this);
        }

        if (shouldSyncCollider)
        {
            SyncColliderNow();
        }
    }

    private void CaptureInitialRotationIfNeeded()
    {
        if (!rememberInitialRotation || initialCaptured || gaussianRenderer == null)
        {
            return;
        }

        initialLocalRotation = gaussianRenderer.localRotation;
        initialCaptured = true;
    }

    private bool TryLoadRotationJson(out LoadedRotation loaded)
    {
        loaded = default;

        if (!TryResolveTransformJsonPath(out string resolvedPath))
        {
            return false;
        }

        if (!File.Exists(resolvedPath))
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Transform JSON file does not exist." + TriffidLog.Fields(("Path", resolvedPath)), this);
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(resolvedPath);
        }
        catch (Exception ex)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Could not read the transform JSON file." + TriffidLog.Fields(("Path", resolvedPath), ("Reason", ex.Message)), this);
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Transform JSON is not valid JSON." + TriffidLog.Fields(("Path", resolvedPath), ("Reason", ex.Message)), this);
            return false;
        }

        JObject colmapToEnu = root["colmap_to_enu"] as JObject;
        if (colmapToEnu == null)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Transform JSON is missing the required 'colmap_to_enu' object.", this);
            return false;
        }

        if (!TryReadDoubleArray(colmapToEnu["R_rowmajor"], 9, "colmap_to_enu.R_rowmajor", out double[] rotationRowMajor))
        {
            return false;
        }

        bool hasScale = TryReadOptionalDouble(colmapToEnu["scale"], out double scale);

        loaded = new LoadedRotation
        {
            ResolvedPath = resolvedPath,
            RotationRowMajor = rotationRowMajor,
            HasScale = hasScale,
            Scale = scale
        };

        return true;
    }

    private bool TryBuildUnityRotation(LoadedRotation loaded, out Quaternion finalRotation)
    {
        finalRotation = Quaternion.identity;

        Matrix4x4 rotationColmapToEnu = BuildRotationMatrixFromRowMajor(loaded.RotationRowMajor);

        if (transposeRotationMatrix)
        {
            rotationColmapToEnu = Transpose3x3(rotationColmapToEnu);
        }

        if (invertRotation)
        {
            rotationColmapToEnu = rotationColmapToEnu.inverse;
        }

        Matrix4x4 enuToUnity = BuildEnuToUnityMatrix();
        Matrix4x4 linear = enuToUnity * rotationColmapToEnu;

        if (!TryExtractRotation(linear, out finalRotation))
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Failed to extract final Unity rotation from JSON matrix.", this);
            return false;
        }

        return true;
    }

    private Matrix4x4 BuildRotationMatrixFromRowMajor(double[] r)
    {
        Matrix4x4 m = Matrix4x4.identity;

        m[0, 0] = (float)r[0];
        m[0, 1] = (float)r[1];
        m[0, 2] = (float)r[2];

        m[1, 0] = (float)r[3];
        m[1, 1] = (float)r[4];
        m[1, 2] = (float)r[5];

        m[2, 0] = (float)r[6];
        m[2, 1] = (float)r[7];
        m[2, 2] = (float)r[8];

        m[0, 3] = 0f;
        m[1, 3] = 0f;
        m[2, 3] = 0f;

        m[3, 0] = 0f;
        m[3, 1] = 0f;
        m[3, 2] = 0f;
        m[3, 3] = 1f;

        return m;
    }

    private Matrix4x4 BuildEnuToUnityMatrix()
    {
        Matrix4x4 m = Matrix4x4.identity;

        Vector3 east;
        Vector3 north;
        Vector3 up;

        switch (enuToUnityAxisMode)
        {
            case EnuToUnityAxisMode.XEast_YNorth_ZUp:
                east = Vector3.right;
                north = Vector3.up;
                up = Vector3.forward;
                break;

            case EnuToUnityAxisMode.Custom:
                east = SafeNormalizedAxis(customEastAxis, Vector3.right, "customEastAxis");
                north = SafeNormalizedAxis(customNorthAxis, Vector3.forward, "customNorthAxis");
                up = SafeNormalizedAxis(customUpAxis, Vector3.up, "customUpAxis");
                break;

            case EnuToUnityAxisMode.XEast_YUp_ZNorth:
            default:
                east = Vector3.right;
                north = Vector3.forward;
                up = Vector3.up;
                break;
        }

        if (flipX)
        {
            east.x *= -1f;
            north.x *= -1f;
            up.x *= -1f;
        }

        if (flipY)
        {
            east.y *= -1f;
            north.y *= -1f;
            up.y *= -1f;
        }

        if (flipZ)
        {
            east.z *= -1f;
            north.z *= -1f;
            up.z *= -1f;
        }

        m.SetColumn(0, new Vector4(east.x, east.y, east.z, 0f));
        m.SetColumn(1, new Vector4(north.x, north.y, north.z, 0f));
        m.SetColumn(2, new Vector4(up.x, up.y, up.z, 0f));
        m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));

        return m;
    }

    private Vector3 SafeNormalizedAxis(Vector3 axis, Vector3 fallback, string axisName)
    {
        if (!IsFinite(axis) || axis.sqrMagnitude < Epsilon * Epsilon)
        {
            TriffidLog.Warning("ColmapGaussianRotationAligner", $"{axisName} is invalid. Using fallback: {fallback}", this);
            return fallback.normalized;
        }

        return axis.normalized;
    }

    private bool TryExtractRotation(Matrix4x4 matrix, out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        Vector3 c0 = GetColumn3(matrix, 0);
        Vector3 c1 = GetColumn3(matrix, 1);
        Vector3 c2 = GetColumn3(matrix, 2);

        float sx = c0.magnitude;
        float sy = c1.magnitude;
        float sz = c2.magnitude;

        if (sx < Epsilon || sy < Epsilon || sz < Epsilon)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", $"Rotation extraction failed because one axis has near-zero magnitude. sx={sx}, sy={sy}, sz={sz}", this);
            return false;
        }

        Vector3 xAxis = c0 / sx;
        Vector3 yAxis = c1 / sy;
        Vector3 zAxis = c2 / sz;

        float handedness = Vector3.Dot(Vector3.Cross(xAxis, yAxis), zAxis);

        if (handedness < 0f)
        {
            zAxis = -zAxis;
        }

        if (!IsFinite(xAxis) || !IsFinite(yAxis) || !IsFinite(zAxis))
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Rotation extraction produced invalid basis vectors.", this);
            return false;
        }

        if (zAxis.sqrMagnitude < Epsilon * Epsilon || yAxis.sqrMagnitude < Epsilon * Epsilon)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", "Rotation extraction produced invalid rotation axes.", this);
            return false;
        }

        rotation = Quaternion.LookRotation(zAxis.normalized, yAxis.normalized);
        return IsFinite(rotation);
    }

    private bool TryResolveTransformJsonPath(out string resolvedPath)
    {
        if (ProjectPathResolver.TryResolveConfiguredPath(
                transformJsonPath,
                inputPathRoot,
                useSharedTransformPathJson,
                sharedTransformPathsJsonFile,
                transformPathJsonKey,
                out resolvedPath,
                out string error))
            return true;

        TriffidLog.Error("ColmapGaussianRotationAligner",
            "Failed to resolve transform path: " + error, this);
        return false;
    }

    private static Matrix4x4 Transpose3x3(Matrix4x4 m)
    {
        Matrix4x4 t = m;

        t[0, 1] = m[1, 0];
        t[0, 2] = m[2, 0];

        t[1, 0] = m[0, 1];
        t[1, 2] = m[2, 1];

        t[2, 0] = m[0, 2];
        t[2, 1] = m[1, 2];

        return t;
    }

    private bool TryReadOptionalDouble(JToken token, out double value)
    {
        value = 0.0;

        if (token == null)
        {
            return false;
        }

        try
        {
            value = token.Value<double>();
            return IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadDoubleArray(JToken token, int expectedCount, string fieldName, out double[] values)
    {
        values = null;

        JArray array = token as JArray;
        if (array == null)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner", $"JSON field '{fieldName}' must be an array.", this);
            return false;
        }

        if (array.Count != expectedCount)
        {
            TriffidLog.Error("ColmapGaussianRotationAligner",
                $"JSON field '{fieldName}' has {array.Count} value(s); expected {expectedCount}.",
                this
            );
            return false;
        }

        values = new double[expectedCount];

        for (int i = 0; i < expectedCount; i++)
        {
            try
            {
                values[i] = array[i].Value<double>();
            }
            catch
            {
                TriffidLog.Error("ColmapGaussianRotationAligner", $"JSON field '{fieldName}[{i}]' is not a number.", this);
                return false;
            }

            if (!IsFinite(values[i]))
            {
                TriffidLog.Error("ColmapGaussianRotationAligner", $"JSON field '{fieldName}[{i}]' is NaN or Infinity.", this);
                return false;
            }
        }

        return true;
    }

    private static string FormatRowMajor3x3(double[] r)
    {
        if (r == null || r.Length != 9)
        {
            return "<invalid>";
        }

        return
            $"[{r[0].ToString("G9", CultureInfo.InvariantCulture)}, {r[1].ToString("G9", CultureInfo.InvariantCulture)}, {r[2].ToString("G9", CultureInfo.InvariantCulture)}]\n" +
            $"[{r[3].ToString("G9", CultureInfo.InvariantCulture)}, {r[4].ToString("G9", CultureInfo.InvariantCulture)}, {r[5].ToString("G9", CultureInfo.InvariantCulture)}]\n" +
            $"[{r[6].ToString("G9", CultureInfo.InvariantCulture)}, {r[7].ToString("G9", CultureInfo.InvariantCulture)}, {r[8].ToString("G9", CultureInfo.InvariantCulture)}]";
    }

    private static Vector3 GetColumn3(Matrix4x4 m, int column)
    {
        Vector4 c = m.GetColumn(column);
        return new Vector3(c.x, c.y, c.z);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }
}
