using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class MarkerInteractionRelay : MonoBehaviour, IMarkerFocusable
{
    private PointData pointData;
    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable interactable;
    private JsonSpawner jsonSpawner;
    private FloatingIcon floatingIcon;
    private bool isManipulating;
    private bool subscribed;
    private static bool missingPointDataWarningLogged;
    private static bool missingInteractableWarningLogged;
    private static bool missingSpawnerWarningLogged;

    public PointData MarkerData => pointData;

    private void Awake()
    {
        pointData = GetComponent<PointData>();
        floatingIcon = GetComponent<FloatingIcon>();
        if (pointData == null)
            LogMissingPointDataOnce();
        TrySubscribe(false);
    }

    private void OnEnable()
    {
        TrySubscribe(false);
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    public void Setup(PointInfoPanel panel)
    {
        if (pointData == null) pointData = GetComponent<PointData>();
        if (pointData == null)
            LogMissingPointDataOnce();
        TrySubscribe(true);
    }

    private void TrySubscribe(bool logIfMissing)
    {
        if (subscribed) return;

        if (interactable == null)
            interactable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>();

        if (interactable == null)
        {
            if (logIfMissing && !missingInteractableWarningLogged)
            {
                Debug.LogWarning("[MarkerInteractionRelay] XRBaseInteractable is missing; marker interaction events cannot be relayed.", this);
                missingInteractableWarningLogged = true;
            }
            return;
        }

        interactable.hoverEntered.AddListener(OnHoverEntered);
        interactable.selectEntered.AddListener(OnSelectEntered);
        interactable.selectExited.AddListener(OnSelectExited);
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || interactable == null) return;

        interactable.hoverEntered.RemoveListener(OnHoverEntered);
        interactable.selectEntered.RemoveListener(OnSelectEntered);
        interactable.selectExited.RemoveListener(OnSelectExited);
        isManipulating = false;
        subscribed = false;
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        FocusMarker(MarkerFocusReason.Hover);
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        isManipulating = true;
        FocusMarker(MarkerFocusReason.Select);
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        if (floatingIcon != null)
        {
            isManipulating = false;
            return;
        }

        ResolveSpawner();
        if (jsonSpawner == null && !missingSpawnerWarningLogged)
        {
            Debug.LogWarning("[MarkerInteractionRelay] JsonSpawner is unavailable; marker movement cannot be persisted.", this);
            missingSpawnerWarningLogged = true;
        }

        if (jsonSpawner != null && pointData != null)
        {
            jsonSpawner.SyncData(pointData, transform);
            jsonSpawner.SaveCurrentStateToPersistentStorage();
            pointData.NotifyDataChanged();
            MarkerEventManager.RaiseMarkerMoved(pointData);
        }

        isManipulating = false;
    }

    public void FocusMarker(MarkerFocusReason reason)
    {
        if (pointData == null) pointData = GetComponent<PointData>();
        if (pointData == null)
        {
            LogMissingPointDataOnce();
            return;
        }

        if (reason == MarkerFocusReason.Hover)
            MarkerEventManager.RaiseMarkerHovered(pointData);
        else
            MarkerEventManager.RaiseMarkerSelected(pointData);
    }

    private void LateUpdate()
    {
        if (!isManipulating)
            return;

        if (floatingIcon != null)
            return;

        ResolveSpawner();
        if (jsonSpawner == null || pointData == null)
            return;

        Transform mapRef = jsonSpawner.mapTransform != null ? jsonSpawner.mapTransform : jsonSpawner.transform;
        Vector3 currentLocal = mapRef.InverseTransformPoint(transform.position);
        JsonSpawner.Vector3Double wgs = jsonSpawner.ColmapToWgs84(currentLocal);

        pointData.latitude = wgs.lat;
        pointData.longitude = wgs.lon;
        pointData.altitude = wgs.alt;

        pointData.NotifyDataChanged();
        MarkerEventManager.RaiseMarkerMoved(pointData);
    }

    private void ResolveSpawner()
    {
        if (jsonSpawner != null)
            return;

        jsonSpawner = FindFirstObjectByType<JsonSpawner>();
    }

    private void LogMissingPointDataOnce()
    {
        if (missingPointDataWarningLogged)
            return;

        Debug.LogWarning("[MarkerInteractionRelay] PointData is missing; marker focus and movement metadata cannot be relayed.", this);
        missingPointDataWarningLogged = true;
    }
}
