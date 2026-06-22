using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

[System.Serializable]
public struct CategorySprite
{
    public string className;
    public Sprite iconSprite;
}

public class PointInfoPanel : MonoBehaviour
{
    private const string ProtectedLatestMqttFeatureId = "latest_mqtt_icon";
    private const string ProtectedLatestMqttClassName = "ugv";
    private const string ProtectedLatestMqttBehaviorFallback = "STOPPED";

    [Header("Marker Text")]
    [Tooltip("Label displayed for the marker confidence field.")]
    [SerializeField] private TextMeshProUGUI confidenceText;
    [Tooltip("Text displaying marker confidence as a percentage.")]
    [SerializeField] private TextMeshProUGUI percentageText;
    [Tooltip("Text displaying the selected feature identifier.")]
    [SerializeField] private TextMeshProUGUI idValueText;
    [Tooltip("Text displaying the selected marker latitude.")]
    [SerializeField] private TextMeshProUGUI latValueText;
    [Tooltip("Text displaying the selected marker altitude.")]
    [SerializeField] private TextMeshProUGUI altitudeValueText;    
    [Tooltip("Text displaying the selected marker longitude.")]
    [SerializeField] private TextMeshProUGUI longValueText;
    [Tooltip("Text displaying the selected marker class.")]
    [SerializeField] private TextMeshProUGUI classText;
    [Tooltip("Text displaying the selected marker source.")]
    [SerializeField] private TextMeshProUGUI sourceText;

    [Header("Category Icons")]
    [Tooltip("Image used to display the icon for the selected marker.")]
    [SerializeField] private Image displayImage;
    [Tooltip("Maps marker class names to the sprites shown in the information panel.")]
    [SerializeField] private List<CategorySprite> spritesList;
    [Tooltip("Fallback sprite used when no class-specific icon matches.")]
    [SerializeField] private Sprite defaultSprite;
    [Tooltip("Sprite reserved for the protected latest-MQTT UGV marker.")]
    [SerializeField] private Sprite ugvSprite;

    [Header("Line Icon")]
    [Tooltip("Sprite displayed for line and polygon annotation nodes.")]
    [SerializeField] private Sprite lineSprite;

    [Header("Confidence UI")]
    [Tooltip("Slider animated to represent the selected marker confidence.")]
    [SerializeField] private Slider confidenceSlider;

    [Header("UGV Behavior UI")]
    [Tooltip("Title label shown for the protected latest-MQTT UGV behavior field.")]
    [SerializeField] private TextMeshProUGUI ugvBehaviorTitleText;
    [Tooltip("Text displaying the current protected UGV behavior value.")]
    [SerializeField] private TextMeshProUGUI ugvBehaviorValueText;

    [Header("Delete UI")]
    [Tooltip("Confirmation button bound to the currently selected marker.")]
    [SerializeField] private DeleteConfirmButton deleteButton;

    [Header("Persistence")]
    [Tooltip("Component implementing IAnnotationPersistenceService for synchronized annotation deletion.")]
    [SerializeField] private MonoBehaviour persistenceProvider;

    [Header("Transitions")]
    [Min(0f)]
    [Tooltip("Duration in seconds used by panel fades and confidence-slider animation.")]
    [SerializeField] private float fadeDuration = 0.25f;
    [Tooltip("CanvasGroup whose alpha is animated during panel transitions.")]
    [SerializeField] private CanvasGroup canvasGroup;

    private class MarkerEntry
    {
        public int id;
        public PointData   data;
        public PulseEffect pulse;
    }

    private class MarkerLifecycleRelay : MonoBehaviour
    {
        public System.Action<int> Destroyed;
        private int _markerId;

        public void Initialize(int markerId)
        {
            _markerId = markerId;
        }

        private void OnDestroy()
        {
            Destroyed?.Invoke(_markerId);
        }
    }

    private readonly Dictionary<int, MarkerEntry> markerEntries = new Dictionary<int, MarkerEntry>();
    private readonly List<int> markerOrder = new List<int>();
    private readonly Dictionary<int, MarkerLifecycleRelay> markerRelays = new Dictionary<int, MarkerLifecycleRelay>();

    private int currentIndex = 0;
    private PointData observedMarker;

    private Coroutine fadeCoroutine;
    private Coroutine sliderCoroutine;
    private IAnnotationPersistenceService persistenceService;
    private bool deleteFallbackWarningLogged;

    private event System.Action<int> markerRemovedEvent;

    private void Awake()
    {
        persistenceService = persistenceProvider as IAnnotationPersistenceService;
        if (persistenceProvider != null && persistenceService == null)
            Debug.LogWarning("[PointInfoPanel] Persistence provider does not implement IAnnotationPersistenceService.");

        ValidateConfiguration();
        markerRemovedEvent += HandleMarkerRemovedEvent;
    }

    private void ValidateConfiguration()
    {
        if (classText == null || idValueText == null || latValueText == null ||
            longValueText == null || altitudeValueText == null)
        {
            Debug.LogWarning("[PointInfoPanel] One or more primary marker text references are missing; some marker details will not be visible.", this);
        }

        if (displayImage == null)
            Debug.LogWarning("[PointInfoPanel] Marker icon image is missing; category icons will not be visible.", this);

        if (spritesList == null)
            Debug.LogWarning("[PointInfoPanel] Category sprite list is missing; class-specific icon lookup is unavailable.", this);

        if (confidenceSlider == null)
            Debug.LogWarning("[PointInfoPanel] Confidence slider is missing; confidence animation will not be visible.", this);

        if (canvasGroup == null)
            Debug.LogWarning("[PointInfoPanel] CanvasGroup is missing; panel fade transitions are unavailable.", this);

        if (deleteButton == null)
        {
            Debug.LogWarning("[PointInfoPanel] Delete button is missing; panel deletion controls are unavailable.", this);
        }
        else if (deleteButton.buttonText == null)
        {
            Debug.LogWarning("[PointInfoPanel] Delete button text is missing; confirmation state will not be visible.", this);
        }

        if (!IsFinite(fadeDuration) || fadeDuration < 0f)
            Debug.LogWarning("[PointInfoPanel] fadeDuration must be finite and non-negative.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnEnable()
    {
        MarkerEventManager.MarkerHovered += HandleExternalMarkerFocus;
        MarkerEventManager.MarkerSelected += HandleExternalMarkerFocus;
    }

    private void OnDisable()
    {
        MarkerEventManager.MarkerHovered -= HandleExternalMarkerFocus;
        MarkerEventManager.MarkerSelected -= HandleExternalMarkerFocus;
        UnbindObservedMarker();

        CancelFadeAndRestoreVisibility();

        if (sliderCoroutine != null)
        {
            StopCoroutine(sliderCoroutine);
            sliderCoroutine = null;
        }
    }

    private void OnDestroy()
    {
        UnbindObservedMarker();
        markerRemovedEvent -= HandleMarkerRemovedEvent;

        foreach (var kv in markerRelays)
        {
            if (kv.Value != null)
                kv.Value.Destroyed -= OnMarkerDestroyed;
        }

        markerRelays.Clear();
    }

    public void InjectPersistence(IAnnotationPersistenceService service)
    {
        persistenceService = service;
        if (service != null)
            deleteFallbackWarningLogged = false;
    }

    public void RegisterMarker(PointData data, PulseEffect pulse)
    {
        if (data == null) return;
        int id = data.GetInstanceID();
        if (markerEntries.ContainsKey(id)) return;

        bool wasEmpty = markerOrder.Count == 0;
        markerEntries[id] = new MarkerEntry { id = id, data = data, pulse = pulse };
        markerOrder.Add(id);
        RegisterLifecycleRelay(data, id);
        Debug.Log($"[Panel] Registered: {data.pointClass} / {data.pointID} - Total: {markerOrder.Count}");

        if (wasEmpty)
        {
            currentIndex = 0;
            RefreshPanel();
        }
    }

    public void UnregisterMarker(PointData data)
    {
        if (data == null) return;
        RemoveMarkerById(data.GetInstanceID(), true);
    }

    public void StopAllPulses()
    {
        foreach (int id in markerOrder)
            if (markerEntries.TryGetValue(id, out MarkerEntry entry) && entry.pulse != null && entry.data != null)
                entry.pulse.StopPulse();
    }

    public void ClearMarkers()
    {
        foreach (int id in markerOrder)
        {
            if (markerEntries.TryGetValue(id, out MarkerEntry entry) && entry.pulse != null && entry.data != null)
                entry.pulse.StopPulse();
        }

        foreach (var kv in markerRelays)
        {
            if (kv.Value != null)
                kv.Value.Destroyed -= OnMarkerDestroyed;
        }

        markerRelays.Clear();
        markerEntries.Clear();
        markerOrder.Clear();
        currentIndex = 0;

        CancelFadeAndRestoreVisibility();
    }

    public void ShowFirst()
    {
        CleanupInvalidMarkers();

        if (markerOrder.Count == 0)
        {
            Debug.LogWarning("[Panel] ShowFirst called but markers list is empty!");
            return;
        }
        currentIndex = 0;
        RefreshPanel();

        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeIn());
    }

    private void CleanupInvalidMarkers()
    {
        for (int i = markerOrder.Count - 1; i >= 0; i--)
        {
            int id = markerOrder[i];
            if (!markerEntries.TryGetValue(id, out MarkerEntry entry) || entry == null || entry.data == null)
                RemoveMarkerById(id, false);
        }

        if (markerOrder.Count == 0)
            currentIndex = 0;
        else
            currentIndex = Mathf.Clamp(currentIndex, 0, markerOrder.Count - 1);
    }

    public void ShowNext()
    {
        if (markerOrder.Count == 0) return;
        StopCurrentPulse();
        currentIndex = (currentIndex + 1) % markerOrder.Count;

        int attempts = 0;
        while (attempts < markerOrder.Count)
        {
            if (TryGetEntryAtIndex(currentIndex, out MarkerEntry entry) && entry.data != null)
                break;

            if (markerOrder.Count == 0)
                break;

            currentIndex = (currentIndex + 1) % markerOrder.Count;
            attempts++;
        }

        if (TryGetEntryAtIndex(currentIndex, out MarkerEntry selected) && selected.data != null)
            UpdateDeleteButtonForMarker(selected.data);

        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeAndRefresh());
    }

    public void ShowPrevious()
    {
        if (markerOrder.Count == 0) return;
        StopCurrentPulse();
        currentIndex = (currentIndex - 1 + markerOrder.Count) % markerOrder.Count;

        int attempts = 0;
        while (attempts < markerOrder.Count)
        {
            if (TryGetEntryAtIndex(currentIndex, out MarkerEntry entry) && entry.data != null)
                break;

            if (markerOrder.Count == 0)
                break;

            currentIndex = (currentIndex - 1 + markerOrder.Count) % markerOrder.Count;
            attempts++;
        }

        if (TryGetEntryAtIndex(currentIndex, out MarkerEntry selected) && selected.data != null)
            UpdateDeleteButtonForMarker(selected.data);

        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeAndRefresh());
    }

    public void DeleteCurrent()
    {
        if (markerOrder.Count == 0)
        {
            RefreshPanel();
            return;
        }

        if (!TryGetEntryAtIndex(currentIndex, out MarkerEntry currentEntry))
        {
            RefreshPanel();
            return;
        }

        PointData targetData = currentEntry.data;
        int targetId = currentEntry.id;
        if (targetData == null)
        {
            RemoveMarkerById(targetId, false);
            RefreshPanel();
            return;
        }

        StopCurrentPulse();

        if (persistenceService != null)
        {
            persistenceService.DeleteFeatureByNode(targetData.transform);
        }
        else
        {
            if (!deleteFallbackWarningLogged)
            {
                Debug.LogWarning("[PointInfoPanel] Delete is using runtime-only fallback because no persistence service is available.", this);
                deleteFallbackWarningLogged = true;
            }

            Destroy(targetData.gameObject);
        }

        RemoveMarkerById(targetId, false);

        if (markerOrder.Count == 0)
        {
            if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
            fadeCoroutine = StartCoroutine(FadeOutThenReset());
            return;
        }

        currentIndex = Mathf.Clamp(currentIndex, 0, markerOrder.Count - 1);

        CancelFadeAndRestoreVisibility();
        RefreshPanel();
    }

    public void OnMarkerSelected(PointData data)
    {
        HandleMarkerSelectedEvent(data);
    }

    private void HandleExternalMarkerFocus(PointData data)
    {
        if (data == null)
            return;

        HandleMarkerSelectedEvent(data);
    }

    private void HandleMarkerSelectedEvent(PointData data)
    {
        if (data == null) return;

        int markerId = data.GetInstanceID();
        int idx = markerOrder.IndexOf(markerId);
        if (idx < 0 || !markerEntries.ContainsKey(markerId))
        {
            Debug.LogWarning($"[Panel] OnMarkerSelected: {data.pointClass} not found!");
            return;
        }

        StopCurrentPulse();
        currentIndex = idx;

        CancelFadeAndRestoreVisibility();
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (!gameObject.activeInHierarchy) return;

        if (markerOrder.Count == 0)
        {
            UnbindObservedMarker();

            if (classText         != null) classText.text         = "-";
            if (idValueText       != null) idValueText.text       = "-";
            if (altitudeValueText != null) altitudeValueText.text = "-";
            if (sourceText        != null) sourceText.text        = "-";
            if (latValueText      != null) latValueText.text      = "-";
            if (longValueText     != null) longValueText.text     = "-";
            if (percentageText    != null) percentageText.text    = "0%";
            if (confidenceText    != null) confidenceText.text    = "Confidence";
            if (deleteButton      != null) deleteButton.SetTargetNode(null);
            SetDeleteButtonVisible(false);
            ClearUgvBehaviorFields();
            if (displayImage      != null)
            {
                displayImage.sprite = null;
                displayImage.enabled = false;
            }
            return;
        }

        if (!TryGetEntryAtIndex(currentIndex, out MarkerEntry currentEntry) || currentEntry.data == null)
        {
            if (markerOrder.Count == 0)
            {
                RefreshPanel();
                return;
            }

            currentIndex = Mathf.Clamp(currentIndex, 0, markerOrder.Count - 1);
            RefreshPanel();
            return;
        }

        PointData p = currentEntry.data;
        BindObservedMarker(p);

        Debug.Log($"[Panel] RefreshPanel -> {p.pointClass} / {p.pointID} (index {currentIndex}/{markerOrder.Count})");

        if (classText         != null) classText.text         = GetDisplayClassName(p);
        if (idValueText       != null) idValueText.text       = p.pointID;
        if (altitudeValueText != null) altitudeValueText.text = $"{p.altitude:F2} m";
        if (sourceText        != null) sourceText.text        = IsProtectedLatestMqttMarker(p) ? string.Empty : p.source;
        if (latValueText      != null) latValueText.text      = $"{Mathf.Abs((float)p.latitude):F4}° {(p.latitude  >= 0 ? "N" : "S")}";
        if (longValueText     != null) longValueText.text     = $"{Mathf.Abs((float)p.longitude):F4}° {(p.longitude >= 0 ? "E" : "W")}";

        float conf01 = Mathf.Clamp01(p.confidence);
        if (percentageText != null) percentageText.text = $"{conf01 * 100:F0}%";
        if (confidenceText != null) confidenceText.text = "Confidence";

        if (confidenceSlider != null)
        {
            if (sliderCoroutine != null)
            {
                StopCoroutine(sliderCoroutine);
                sliderCoroutine = null;
            }

            sliderCoroutine = StartCoroutine(AnimateSlider(confidenceSlider.value, conf01));
        }

        if (displayImage != null)
        {
            Sprite spriteToShow = defaultSprite;
            if (IsProtectedLatestMqttMarker(p) && ugvSprite != null)
            {
                spriteToShow = ugvSprite;
            }
            else if (IsLineMarker(p) && lineSprite != null)
            {
                spriteToShow = lineSprite;
            }
            else
            {
                foreach (var item in spritesList)
                {
                    if (string.Equals(item.className, p.pointClass, System.StringComparison.OrdinalIgnoreCase))
                    {
                        spriteToShow = item.iconSprite;
                        break;
                    }
                }
            }
            displayImage.sprite  = spriteToShow;
            displayImage.enabled = spriteToShow != null;
        }

        UpdateUgvBehaviorFields(p);
        UpdateDeleteButtonForMarker(p);

        StartCurrentPulse();
    }

    private void StopCurrentPulse()
    {
        if (TryGetEntryAtIndex(currentIndex, out MarkerEntry entry) && entry.pulse != null && entry.data != null)
            entry.pulse.StopPulse();
    }

    private void StartCurrentPulse()
    {
        foreach (int id in markerOrder)
        {
            if (markerEntries.TryGetValue(id, out MarkerEntry entry) && entry.pulse != null && entry.data != null)
                entry.pulse.StopPulse();
        }

        if (TryGetEntryAtIndex(currentIndex, out MarkerEntry current) && current.pulse != null && current.data != null)
            current.pulse.StartPulse();
    }

    private IEnumerator AnimateSlider(float from, float to)
    {
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            confidenceSlider.value = Mathf.Lerp(from, to, elapsed / fadeDuration);
            yield return null;
        }
        confidenceSlider.value = to;
        sliderCoroutine = null;
    }

    private IEnumerator FadeAndRefresh()
    {
        yield return FadeOut();
        RefreshPanel();
        yield return FadeIn();
        fadeCoroutine = null;
    }

    private IEnumerator FadeIn()
    {
        if (canvasGroup == null) yield break;
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = elapsed / fadeDuration;
            yield return null;
        }
        canvasGroup.alpha = 1f;
    }

    private IEnumerator FadeOut()
    {
        if (canvasGroup == null) yield break;
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = 1f - (elapsed / fadeDuration);
            yield return null;
        }
        canvasGroup.alpha = 0f;
    }

    private IEnumerator FadeOutThenReset()
    {
        yield return FadeOut();
        RefreshPanel(); 
        yield return FadeIn();
        fadeCoroutine = null;
    }

    private void CancelFadeAndRestoreVisibility()
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        if (canvasGroup != null)
            canvasGroup.alpha = 1f;
    }

    internal bool IsObservingMarker(PointData data)
    {
        return observedMarker == data;
    }

    public void LiveUpdateIfCurrent(PointData data)
    {
        if (data == null) return;
        if (!gameObject.activeInHierarchy || markerOrder.Count == 0)
            return;

        int incomingId = data.GetInstanceID();
        int incomingIndex = markerOrder.IndexOf(incomingId);
        if (incomingIndex >= 0)
        {
            if (currentIndex != incomingIndex)
                currentIndex = incomingIndex;

            RefreshPanel();
            return;
        }

        if (TryGetEntryAtIndex(currentIndex, out MarkerEntry current) && current.data == data)
        {
            RefreshPanel();
        }
    }

    private void RegisterLifecycleRelay(PointData data, int markerId)
    {
        if (data == null)
            return;

        MarkerLifecycleRelay relay = data.GetComponent<MarkerLifecycleRelay>();
        if (relay == null)
            relay = data.gameObject.AddComponent<MarkerLifecycleRelay>();

        relay.Initialize(markerId);
        relay.Destroyed -= OnMarkerDestroyed;
        relay.Destroyed += OnMarkerDestroyed;
        markerRelays[markerId] = relay;
    }

    private void OnMarkerDestroyed(int markerId)
    {
        markerRemovedEvent?.Invoke(markerId);
    }

    private void HandleMarkerRemovedEvent(int markerId)
    {
        RemoveMarkerById(markerId, false);
    }

    private bool TryGetEntryAtIndex(int index, out MarkerEntry entry)
    {
        entry = null;
        if (index < 0 || index >= markerOrder.Count)
            return false;

        int id = markerOrder[index];
        if (!markerEntries.TryGetValue(id, out entry))
        {
            markerOrder.RemoveAt(index);
            if (markerOrder.Count == 0)
                currentIndex = 0;
            else
                currentIndex = Mathf.Clamp(currentIndex, 0, markerOrder.Count - 1);
            return false;
        }

        if (entry.data != null)
            return true;

        RemoveMarkerById(id, false);
        entry = null;
        return false;
    }

    private void RemoveMarkerById(int markerId, bool stopPulse)
    {
        if (markerEntries.TryGetValue(markerId, out MarkerEntry entry))
        {
            if (entry.data != null && observedMarker == entry.data)
                UnbindObservedMarker();

            if (stopPulse && entry.pulse != null && entry.data != null)
                entry.pulse.StopPulse();

            markerEntries.Remove(markerId);
        }

        int idx = markerOrder.IndexOf(markerId);
        if (idx >= 0)
            markerOrder.RemoveAt(idx);

        if (markerRelays.TryGetValue(markerId, out MarkerLifecycleRelay relay))
        {
            if (relay != null)
                relay.Destroyed -= OnMarkerDestroyed;
            markerRelays.Remove(markerId);
        }

        if (markerOrder.Count > 0)
            currentIndex = Mathf.Clamp(currentIndex, 0, markerOrder.Count - 1);
        else
            currentIndex = 0;

        if (deleteButton != null && markerOrder.Count == 0)
        {
            deleteButton.SetTargetNode(null);
            SetDeleteButtonVisible(false);
        }
    }

    private void UpdateDeleteButtonForMarker(PointData marker)
    {
        if (deleteButton == null)
            return;

        if (marker == null || IsProtectedLatestMqttMarker(marker))
        {
            deleteButton.SetTargetNode(null);
            SetDeleteButtonVisible(false);
            return;
        }

        deleteButton.SetTargetNode(marker.transform);
        SetDeleteButtonVisible(true);
    }

    private void UpdateUgvBehaviorFields(PointData marker)
    {
        if (marker == null)
        {
            ClearUgvBehaviorFields();
            return;
        }

        bool isUgv = IsProtectedLatestMqttMarker(marker);
        string titleText = isUgv ? "BEHAVIOR" : "id";
        string valueText = isUgv
            ? (string.IsNullOrWhiteSpace(marker.behavior) ? ProtectedLatestMqttBehaviorFallback : marker.behavior)
            : marker.pointID;

        if (ugvBehaviorTitleText != null)
            ugvBehaviorTitleText.text = titleText;

        if (ugvBehaviorValueText != null)
            ugvBehaviorValueText.text = valueText;
    }

    private void ClearUgvBehaviorFields()
    {
        if (ugvBehaviorTitleText != null)
            ugvBehaviorTitleText.text = "-";

        if (ugvBehaviorValueText != null)
            ugvBehaviorValueText.text = "-";
    }

    private bool IsProtectedLatestMqttMarker(PointData marker)
    {
        if (marker == null)
            return false;

        if (string.Equals(marker.pointID, ProtectedLatestMqttFeatureId, System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(marker.pointClass, ProtectedLatestMqttClassName, System.StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void SetDeleteButtonVisible(bool visible)
    {
        if (deleteButton == null)
            return;

        if (deleteButton.gameObject.activeSelf != visible)
            deleteButton.gameObject.SetActive(visible);
    }

    private bool IsLineMarker(PointData p)
    {
        if (p == null) return false;

        string pointClass = p.pointClass ?? "";
        string category   = p.category ?? "";
        string objName    = p.gameObject != null ? p.gameObject.name : "";

        if (objName.StartsWith("Corner_", System.StringComparison.OrdinalIgnoreCase) ||
            objName.StartsWith("Node_", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (pointClass.StartsWith("Drawing", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(category, "Annotation", System.StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private string GetDisplayClassName(PointData p)
    {
        if (p == null)
            return "-";

        if (IsLineMarker(p) && !string.IsNullOrWhiteSpace(p.lineColorHex) && ColorUtility.TryParseHtmlString(p.lineColorHex, out Color lineColor))
            return $"Line {GetNearestNamedColor(lineColor)}";

        return p.pointClass;
    }

    private static string GetNearestNamedColor(Color color)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);

        if (v < 0.15f) return "Black";
        if (s < 0.12f)
        {
            if (v > 0.88f) return "White";
            return "Gray";
        }

        float hue = h * 360f;
        if (hue < 15f || hue >= 345f) return "Red";
        if (hue < 40f) return "Orange";
        if (hue < 70f) return "Yellow";
        if (hue < 105f) return "Lime";
        if (hue < 165f) return "Green";
        if (hue < 200f) return "Cyan";
        if (hue < 255f) return "Blue";
        if (hue < 285f) return "Purple";
        if (hue < 330f) return "Magenta";
        return "Pink";
    }

    private void BindObservedMarker(PointData marker)
    {
        if (observedMarker == marker)
            return;

        UnbindObservedMarker();
        observedMarker = marker;

        if (observedMarker != null)
            observedMarker.OnDataChanged += OnObservedMarkerDataChanged;
    }

    private void UnbindObservedMarker()
    {
        if (observedMarker != null)
            observedMarker.OnDataChanged -= OnObservedMarkerDataChanged;

        observedMarker = null;
    }

    private void OnObservedMarkerDataChanged()
    {
        if (observedMarker == null)
            return;

        LiveUpdateIfCurrent(observedMarker);
    }
}
