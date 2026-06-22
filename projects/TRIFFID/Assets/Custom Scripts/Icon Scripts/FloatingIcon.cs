using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections;

public class FloatingIcon : MonoBehaviour, IMarkerFocusable
{
    [Header("Runtime Map References")]
    [Tooltip("Map transform used to keep the icon anchored to a local map position. Normally assigned by JsonSpawner.Setup.")]
    public Transform mainMap;
    [Tooltip("Icon position in map-local coordinates. Normally initialized at runtime by Setup.")]
    public Vector3 localMapPoint;
    private JsonSpawner mySpawner;

    [Header("Floating Visual")]
    [Tooltip("Vertical distance in world units used by the idle floating animation.")]
    public float floatAmplitude = 0.2f;
    [Tooltip("Number of idle floating cycles per second.")]
    public float floatFrequency = 1f;

    [Header("Live Coordinate Updates")]
    [Min(0f)]
    [Tooltip("Seconds between coordinate refreshes while the marker is actively manipulated.")]
    [SerializeField] private float geoUpdateInterval = 0.1f;

    private bool isBeingManipulated = false;
    private PointData myPointData;
    private Coroutine geoUpdateCoroutine;
    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable;
    private bool xrGrabLookupAttempted;
    private bool manipulationDrivenByGrabSelectedFallback;
    private PointInfoPanel infoPanel;
    private static bool missingTargetMapWarningLogged;
    private static bool missingSpawnerWarningLogged;
    private static bool missingPointDataWarningLogged;
    private static bool missingCameraWarningLogged;
    private static bool missingGrabInteractableWarningLogged;

    public PointData MarkerData => myPointData;
    public bool IsBeingManipulated => isBeingManipulated;

    public void Setup(Transform targetMap, JsonSpawner spawner)
    {
        mainMap   = targetMap;
        mySpawner = spawner;
        infoPanel = spawner != null ? spawner.infoPanel : null;
        ResolveGrabInteractableOnce();

        if (mainMap != null)
            localMapPoint = mainMap.InverseTransformPoint(transform.position);

        myPointData = GetComponent<PointData>();

        ValidateRuntimeSetup();

        Debug.Log($"[FloatingIcon] Setup: {gameObject.name} | PointData: {(myPointData != null ? myPointData.pointClass : "NULL")}");
    }

    private void ValidateRuntimeSetup()
    {
        if (mainMap == null && !missingTargetMapWarningLogged)
        {
            Debug.LogWarning("[FloatingIcon] Missing target map; floating position updates are unavailable.", this);
            missingTargetMapWarningLogged = true;
        }

        if (mySpawner == null && !missingSpawnerWarningLogged)
        {
            Debug.LogWarning("[FloatingIcon] JsonSpawner is missing; coordinate synchronization is unavailable.", this);
            missingSpawnerWarningLogged = true;
        }

        if (myPointData == null && !missingPointDataWarningLogged)
        {
            Debug.LogWarning("[FloatingIcon] PointData is missing; marker metadata updates are unavailable.", this);
            missingPointDataWarningLogged = true;
        }

        if (Camera.main == null && !missingCameraWarningLogged)
        {
            Debug.LogWarning("[FloatingIcon] Camera.main is missing; billboard facing is unavailable.", this);
            missingCameraWarningLogged = true;
        }

        if (grabInteractable == null && !missingGrabInteractableWarningLogged)
        {
            Debug.LogWarning("[FloatingIcon] XRGrabInteractable is missing; selected-state manipulation fallback is unavailable.", this);
            missingGrabInteractableWarningLogged = true;
        }

        if (float.IsNaN(geoUpdateInterval) || float.IsInfinity(geoUpdateInterval) || geoUpdateInterval < 0f)
            Debug.LogWarning("[FloatingIcon] geoUpdateInterval must be finite and non-negative.", this);
    }

    public void OnHoverEntered(HoverEnterEventArgs args)
    {
        FocusMarker(MarkerFocusReason.Hover);

        if (infoPanel != null && myPointData != null)
            infoPanel.OnMarkerSelected(myPointData);
    }

    public void OnSelectEntered(SelectEnterEventArgs args)
    {
        isBeingManipulated = true;
        manipulationDrivenByGrabSelectedFallback = false;
        FocusMarker(MarkerFocusReason.Select);

        if (infoPanel != null && myPointData != null)
            infoPanel.OnMarkerSelected(myPointData);

        if (geoUpdateCoroutine != null)
            StopCoroutine(geoUpdateCoroutine);

        geoUpdateCoroutine = StartCoroutine(UpdateGeoDataWhileManipulating());
    }

    public void OnManipulationEnded(SelectExitEventArgs args)
    {
        isBeingManipulated = false;
        manipulationDrivenByGrabSelectedFallback = false;

        if (geoUpdateCoroutine != null)
        {
            StopCoroutine(geoUpdateCoroutine);
            geoUpdateCoroutine = null;
        }

        if (mainMap != null)
            localMapPoint = mainMap.InverseTransformPoint(transform.position);

        if (mySpawner != null)
        {
            if (myPointData == null)
                myPointData = GetComponent<PointData>();

            if (myPointData != null)
                mySpawner.SyncData(myPointData, transform);

            mySpawner.SaveCurrentStateToPersistentStorage();
        }

        if (myPointData != null)
        {
            myPointData.NotifyDataChanged();
            MarkerEventManager.RaiseMarkerMoved(myPointData);
        }
    }

    public void FocusMarker(MarkerFocusReason reason)
    {
        if (myPointData == null)
            myPointData = GetComponent<PointData>();

        if (myPointData == null)
            return;

        Debug.Log($"[FloatingIcon] {reason}: {gameObject.name} / PointData: {myPointData.pointID}");

        if (reason == MarkerFocusReason.Hover)
            MarkerEventManager.RaiseMarkerHovered(myPointData);
        else
            MarkerEventManager.RaiseMarkerSelected(myPointData);
    }

    private IEnumerator UpdateGeoDataWhileManipulating()
    {
        while (isBeingManipulated)
        {
            RefreshLiveRuntimeData();

            float interval = geoUpdateInterval > 0.01f ? geoUpdateInterval : 0.01f;
            yield return new WaitForSeconds(interval);
        }

        geoUpdateCoroutine = null;
    }

    private void RefreshLiveRuntimeData()
    {
        if (!isBeingManipulated || mainMap == null || myPointData == null || mySpawner == null)
            return;

        Vector3 currentLocal = mainMap.InverseTransformPoint(transform.position);
        JsonSpawner.Vector3Double wgs = mySpawner.ColmapToWgs84(currentLocal);

        myPointData.latitude = wgs.lat;
        myPointData.longitude = wgs.lon;
        myPointData.altitude = wgs.alt;

        myPointData.NotifyDataChanged();
        if (infoPanel != null && !infoPanel.IsObservingMarker(myPointData))
            infoPanel.LiveUpdateIfCurrent(myPointData);

        MarkerEventManager.RaiseMarkerMoved(myPointData);
    }

    private void OnDisable()
    {
        isBeingManipulated = false;
        if (geoUpdateCoroutine != null)
        {
            StopCoroutine(geoUpdateCoroutine);
            geoUpdateCoroutine = null;
        }
    }

    private void Update()
    {
        ResolveGrabInteractableOnce();

        if (grabInteractable == null)
            return;

        if (grabInteractable.isSelected && !isBeingManipulated)
        {
            isBeingManipulated = true;
            manipulationDrivenByGrabSelectedFallback = true;

            if (infoPanel != null && myPointData != null)
                infoPanel.OnMarkerSelected(myPointData);

            if (geoUpdateCoroutine != null)
                StopCoroutine(geoUpdateCoroutine);

            geoUpdateCoroutine = StartCoroutine(UpdateGeoDataWhileManipulating());
        }
        else if (!grabInteractable.isSelected && isBeingManipulated && manipulationDrivenByGrabSelectedFallback)
        {
            isBeingManipulated = false;
            manipulationDrivenByGrabSelectedFallback = false;

            if (geoUpdateCoroutine != null)
            {
                StopCoroutine(geoUpdateCoroutine);
                geoUpdateCoroutine = null;
            }

            if (mainMap != null)
                localMapPoint = mainMap.InverseTransformPoint(transform.position);

            if (mySpawner != null && myPointData != null)
            {
                mySpawner.SyncData(myPointData, transform);
                mySpawner.SaveCurrentStateToPersistentStorage();
                myPointData.NotifyDataChanged();
                MarkerEventManager.RaiseMarkerMoved(myPointData);
            }
        }
    }

    private void ResolveGrabInteractableOnce()
    {
        if (xrGrabLookupAttempted)
            return;

        xrGrabLookupAttempted = true;
        grabInteractable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
    }

    void LateUpdate()
    {
        if (isBeingManipulated)
        {
            RefreshLiveRuntimeData();
            return;
        }

        if (mainMap == null) return;

        Vector3 basePosition = mainMap.TransformPoint(localMapPoint);
        float floatOffset    = Mathf.Sin(Time.time * Mathf.PI * 2f * floatFrequency) * floatAmplitude;

        transform.position = new Vector3(basePosition.x, basePosition.y + floatOffset, basePosition.z);

        if (Camera.main != null)
            transform.LookAt(Camera.main.transform);
    }
}
