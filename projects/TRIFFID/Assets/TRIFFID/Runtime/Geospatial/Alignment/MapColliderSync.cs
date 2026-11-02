using UnityEngine;

/// <summary>
/// Keeps mesh vertices in the renderer's coordinate frame. Parenting preserves
/// reflections and scaled/rotated ancestors without decomposing world matrices.
/// </summary>
public class MapColliderSync : MonoBehaviour
{
    public enum MeshVertexAxes
    {
        OriginalPly = 0,
        AlreadyMirroredX = 1
    }

    [Header("Renderer and Collider")]
    [Tooltip("Source of the complete display transform. Keep its required scale, including (-1, 1, 1).")]
    public Transform GaussianRenderer;

    [Tooltip("Dedicated mesh object to place directly below Gaussian Renderer. It must not contain the renderer.")]
    public Transform MeshColliderObject;

    [Header("Mesh Coordinates")]
    [Tooltip("OriginalPly: unchanged XYZ, as exported by the converter. AlreadyMirroredX: mesh vertices already contain (-x, y, z); undo that import conversion locally.")]
    public MeshVertexAxes meshVertexAxes = MeshVertexAxes.OriginalPly;

    [Header("Binding")]
    [Tooltip("Bind on Start and repair the binding in LateUpdate. Once parented, transforms are inherited even when this option is off. The mesh is never rebuilt or reloaded.")]
    public bool maintainBinding = true;

    // Keep the existing public/serialized reference for compatibility. It no
    // longer supplies position or scale; GaussianRenderer is the only source.
    [HideInInspector] public Transform MapTransform;

    private string lastWarning;

    private void Start()
    {
        if (maintainBinding) SyncToMap();
    }

    private void LateUpdate()
    {
        if (maintainBinding) SyncToMap();
    }

    public bool TryValidateSetup(out string error)
    {
        if (GaussianRenderer == null)
            error = "Assign Gaussian Renderer as the transform source.";
        else if (MeshColliderObject == null)
            error = "Assign a dedicated Mesh Collider Object.";
        else if (MeshColliderObject == GaussianRenderer || GaussianRenderer.IsChildOf(MeshColliderObject))
            error = "The mesh cannot be the renderer or an ancestor of it. Use a separate mesh object.";
        else if (meshVertexAxes != MeshVertexAxes.OriginalPly && meshVertexAxes != MeshVertexAxes.AlreadyMirroredX)
            error = "Choose a supported Mesh Vertex Axes value.";
        else
        {
            error = null;
            return true;
        }
        return false;
    }

    [ContextMenu("Match Gaussian Renderer")]
    public void SyncToMap()
    {
        // Public calls from the aligner must also honor a disabled component.
        if (!enabled) return;
        if (!TryValidateSetup(out string error))
        {
            if (lastWarning != error) Debug.LogWarning("[MapColliderSync] " + error, this);
            lastWarning = error;
            return;
        }
        lastWarning = null;

        // Do not decompose a mirrored/sheared world matrix. The desired local
        // transform is explicit and independent of the target's old hierarchy.
        if (MeshColliderObject.parent != GaussianRenderer)
            MeshColliderObject.SetParent(GaussianRenderer, false);
        Vector3 localScale = meshVertexAxes == MeshVertexAxes.AlreadyMirroredX
            ? new Vector3(-1f, 1f, 1f) : Vector3.one;
        if (MeshColliderObject.localPosition != Vector3.zero)
            MeshColliderObject.localPosition = Vector3.zero;
        if (MeshColliderObject.localRotation != Quaternion.identity)
            MeshColliderObject.localRotation = Quaternion.identity;
        if (MeshColliderObject.localScale != localScale)
            MeshColliderObject.localScale = localScale;
    }
}
