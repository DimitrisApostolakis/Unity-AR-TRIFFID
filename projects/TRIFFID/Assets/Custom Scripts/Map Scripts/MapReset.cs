using UnityEngine;

public class MapReset : MonoBehaviour
{
    [Header("Map / XR Rig References")]
    [SerializeField] private GameObject mapObject;
    [SerializeField] private GameObject xrRig;
    [SerializeField] private XRControllerLogger controllerLogger;
    
    private Vector3 mapInitialPosition;
    private Quaternion mapInitialRotation;
    private Vector3 mapInitialScale;
    private Vector3 xrInitialPosition;
    private Quaternion xrInitialRotation;
    private Vector3 xrInitialScale; 
    private bool missingControllerWarningLogged;

    void Awake()
    {
        if (mapObject != null)
        {
            mapInitialPosition = mapObject.transform.position;
            mapInitialRotation = mapObject.transform.rotation;
            mapInitialScale = mapObject.transform.localScale; 
        }
        else
        {
            Debug.LogWarning("[MapReset] Map target is missing; map reset cannot run.", this);
        }

        if (xrRig != null)
        {
            xrInitialPosition = xrRig.transform.position;
            xrInitialRotation = xrRig.transform.rotation;
            xrInitialScale = xrRig.transform.localScale; 
        }
        else
        {
            Debug.LogWarning("[MapReset] XR rig target is missing; XR rig reset cannot run.", this);
        }

        if (controllerLogger == null)
        {
            Debug.LogWarning("[MapReset] XRControllerLogger is not assigned; menu recenter will be skipped.", this);
            missingControllerWarningLogged = true;
        }
    }

    public void ResetMap()
    {
        if (mapObject != null)
        {
            mapObject.transform.position = mapInitialPosition;
            mapObject.transform.rotation = mapInitialRotation;
            mapObject.transform.localScale = mapInitialScale; 
        }

        if (xrRig != null)
        {
            xrRig.transform.position = xrInitialPosition;
            xrRig.transform.rotation = xrInitialRotation;
            xrRig.transform.localScale = xrInitialScale; 
        }

        if (controllerLogger != null)
        {
            missingControllerWarningLogged = false;
            controllerLogger.RecenterMenuNearUser();
        }
        else if (!missingControllerWarningLogged)
        {
            Debug.LogWarning("[MapReset] XRControllerLogger is not assigned; menu recenter will be skipped.", this);
            missingControllerWarningLogged = true;
        }
        
    }
}
