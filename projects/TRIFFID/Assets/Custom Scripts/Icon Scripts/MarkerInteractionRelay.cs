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
    [SerializeField, Min(0.01f)] private float liveUpdateInterval = 0.1f;
    [SerializeField, Min(0f)] private float movementEpsilon = 0.0001f;
    private float nextLiveUpdateTime;
    private Vector3 lastLocalPosition;
    private bool hasLastLocalPosition;
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
        CacheCurrentLocalPosition();
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
        CacheCurrentLocalPosition();
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
        if (subscribed && interactable != null)
        {
            interactable.hoverEntered.RemoveListener(OnHoverEntered);
            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.selectExited.RemoveListener(OnSelectExited);
        }

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
        nextLiveUpdateTime = 0f;
        CacheCurrentLocalPosition();
        FocusMarker(MarkerFocusReason.Select);
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        // Always take one uncapped sample before persistence. This relay is the
        // common runtime path for server markers and locally drawn annotations.
        RefreshRuntimeData(true);
        isManipulating = false;

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
        RefreshRuntimeData(false);
    }

    private bool RefreshRuntimeData(bool force)
    {
        if (pointData == null)
            pointData = GetComponent<PointData>();

        ResolveSpawner();
        if (jsonSpawner == null || pointData == null)
            return false;

        Vector3 currentObjectLocal = transform.localPosition;
        float epsilon = Mathf.Max(0f, movementEpsilon);
        bool positionChanged = !hasLastLocalPosition ||
                               (currentObjectLocal - lastLocalPosition).sqrMagnitude > epsilon * epsilon;

        // XR callbacks are useful when available, but positionChanged keeps live
        // updates working for every runtime prefab even when callback wiring differs.
        if (!force && !isManipulating && !positionChanged)
            return false;

        if (!force && Time.unscaledTime < nextLiveUpdateTime)
            return false;

        nextLiveUpdateTime = Time.unscaledTime + Mathf.Max(0.01f, liveUpdateInterval);
        lastLocalPosition = currentObjectLocal;
        hasLastLocalPosition = true;

        Transform mapRef = jsonSpawner.mapTransform != null ? jsonSpawner.mapTransform : jsonSpawner.transform;
        Vector3 currentMapLocal = mapRef.InverseTransformPoint(transform.position);

        // Keep FloatingIcon's cached map position aligned with the transform so a
        // later SyncWorldToJSON cannot restore stale coordinates or stale height.
        if (floatingIcon == null)
            floatingIcon = GetComponent<FloatingIcon>();

        if (floatingIcon != null)
        {
            floatingIcon.mainMap = mapRef;
            floatingIcon.localMapPoint = currentMapLocal;
        }

        JsonSpawner.Vector3Double wgs = jsonSpawner.ColmapToWgs84(currentMapLocal);

        pointData.latitude = wgs.lat;
        pointData.longitude = wgs.lon;
        pointData.altitude = wgs.alt;
        jsonSpawner.RefreshLiveHeightAboveSurface(pointData, transform);

        pointData.NotifyDataChanged();
        MarkerEventManager.RaiseMarkerMoved(pointData);
        return true;
    }

    private void CacheCurrentLocalPosition()
    {
        lastLocalPosition = transform.localPosition;
        hasLastLocalPosition = true;
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
