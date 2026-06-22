using UnityEngine;

public class MapColliderSync : MonoBehaviour
{
    public Transform MapTransform;
    public Transform MeshColliderObject;

    [SerializeField] private Vector3 rotationOffsetEuler = new Vector3(123f, 0f, 0f);
    private bool missingMapWarningLogged;
    private bool missingColliderWarningLogged;

    public void SyncToMap()
    {
        bool canSync = true;
        if (MapTransform == null)
        {
            if (!missingMapWarningLogged)
            {
                Debug.LogWarning("[MapColliderSync] Map transform is missing; collider sync cannot run.", this);
                missingMapWarningLogged = true;
            }
            canSync = false;
        }
        else
        {
            missingMapWarningLogged = false;
        }

        if (MeshColliderObject == null)
        {
            if (!missingColliderWarningLogged)
            {
                Debug.LogWarning("[MapColliderSync] Mesh collider transform is missing; collider sync cannot run.", this);
                missingColliderWarningLogged = true;
            }
            canSync = false;
        }
        else
        {
            missingColliderWarningLogged = false;
        }

        if (!canSync)
            return;

        MeshColliderObject.position   = MapTransform.position;
        MeshColliderObject.localScale = MapTransform.localScale;
        MeshColliderObject.rotation   = MapTransform.rotation * Quaternion.Euler(rotationOffsetEuler);
    }
}
