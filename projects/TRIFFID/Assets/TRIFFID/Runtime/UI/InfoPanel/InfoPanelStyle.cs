using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Presentation layer for the info panel and menu captions. It attaches itself
// at runtime and finds its targets by their existing object names, so no
// prefab, scene or layout is edited and PointInfoPanel keeps its own logic.
//
// - The class title takes the marker's group colour (or the drawn line's own
//   colour), matching the footprint ring on the map.
// - The confidence bar takes the same band colour as the percentage.
// - The icon settles with a short scale-in when a different marker is shown,
//   so a content swap is noticed. Live updates of one marker never animate.
// - Filter, spawn and polygon-class captions take their group colour, so the
//   menus double as the legend for the map.
[DisallowMultipleComponent]
public class InfoPanelStyle : MonoBehaviour
{
    private const string ClassLabelName = "Class";
    private const string IconName = "IconImage";
    private const float IconPopSeconds = 0.18f;
    private const float IconPopStartScale = 0.86f;

    private PointInfoPanel panel;
    private TextMeshProUGUI classLabel;
    private Graphic confidenceFill;
    private Transform icon;

    private Color classLabelColor;
    private Color confidenceFillColor;
    private Vector3 iconScale = Vector3.one;
    private bool resolved;

    private PointData lastMarker;
    private Coroutine popRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        ApplyToLoadedScenes();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ApplyToLoadedScenes();
    }

    private static void ApplyToLoadedScenes()
    {
        TriffidVisualSettings settings = TriffidVisualSettings.Current;

        if (settings.infoPanelStyling)
        {
            PointInfoPanel[] panels = FindObjectsByType<PointInfoPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (PointInfoPanel infoPanel in panels)
                if (infoPanel != null && infoPanel.GetComponent<InfoPanelStyle>() == null)
                    infoPanel.gameObject.AddComponent<InfoPanelStyle>();
        }

        if (settings.colorMenuCaptions)
            ColorMenuCaptions();
    }

    private static void ColorMenuCaptions()
    {
        TextMeshProUGUI[] labels = FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (TextMeshProUGUI label in labels)
        {
            if (label == null)
                continue;

            string caption = label.text;
            Color color;
            // Captions already carrying markup were handled on an earlier scene load.
            if (string.IsNullOrEmpty(caption) || caption.IndexOf('<') >= 0 ||
                !SemanticPalette.TryGetMenuLabelColor(caption, out color))
                continue;

            // The panel's own fields are data, not legend entries.
            if (label.GetComponentInParent<PointInfoPanel>(true) != null)
                continue;

            label.text = "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + caption + "</color>";
        }
    }

    private void OnEnable()
    {
        if (panel == null)
            panel = GetComponent<PointInfoPanel>();
        if (panel == null)
            return;

        ResolveTargets();
        panel.MarkerPresented -= OnMarkerPresented;
        panel.MarkerPresented += OnMarkerPresented;

        // The panel refreshes in its own OnEnable, before this subscription exists.
        lastMarker = panel.PresentedMarker;
        ApplyColors(lastMarker);
    }

    private void OnDisable()
    {
        if (panel != null)
            panel.MarkerPresented -= OnMarkerPresented;

        StopPop();
    }

    private void ResolveTargets()
    {
        if (resolved)
            return;

        resolved = true;

        foreach (TextMeshProUGUI label in GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (label != null && label.gameObject.name == ClassLabelName)
            {
                classLabel = label;
                classLabelColor = label.color;
                break;
            }
        }

        Slider slider = GetComponentInChildren<Slider>(true);
        if (slider != null && slider.fillRect != null)
        {
            confidenceFill = slider.fillRect.GetComponent<Graphic>();
            if (confidenceFill != null)
                confidenceFillColor = confidenceFill.color;
        }

        foreach (Image image in GetComponentsInChildren<Image>(true))
        {
            if (image != null && image.gameObject.name == IconName)
            {
                icon = image.transform;
                iconScale = icon.localScale;
                break;
            }
        }
    }

    private void OnMarkerPresented(PointData marker)
    {
        ApplyColors(marker);

        if (marker != null && marker != lastMarker)
            StartPop();

        lastMarker = marker;
    }

    private void ApplyColors(PointData marker)
    {
        if (classLabel != null)
            classLabel.color = marker != null ? TitleColor(marker) : classLabelColor;

        if (confidenceFill != null)
        {
            Color fill = marker != null ? SemanticPalette.ForConfidence(Mathf.Clamp01(marker.confidence)) : confidenceFillColor;
            fill.a = confidenceFillColor.a;
            confidenceFill.color = fill;
        }
    }

    private Color TitleColor(PointData marker)
    {
        Color color;
        if (MissionPathColors.TryGetColor(marker.pointClass, out color))
            return color;

        if (SemanticPalette.TryGetClassColor(marker.pointClass, out color))
            return color;

        // A drawn line or polygon vertex: show the colour it has on the map.
        if (!string.IsNullOrWhiteSpace(marker.lineColorHex) && ColorUtility.TryParseHtmlString(marker.lineColorHex, out color))
        {
            // Keep dark strokes readable on the dark panel.
            float h, s, v;
            Color.RGBToHSV(color, out h, out s, out v);
            color = Color.HSVToRGB(h, s, Mathf.Max(v, 0.75f));
            color.a = 1f;
            return color;
        }

        return classLabelColor;
    }

    private void StartPop()
    {
        if (icon == null || !isActiveAndEnabled)
            return;

        StopPop();
        popRoutine = StartCoroutine(PopIcon());
    }

    private void StopPop()
    {
        if (popRoutine != null)
        {
            StopCoroutine(popRoutine);
            popRoutine = null;
        }

        if (icon != null)
            icon.localScale = iconScale;
    }

    private IEnumerator PopIcon()
    {
        float elapsed = 0f;
        while (elapsed < IconPopSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float remaining = 1f - Mathf.Clamp01(elapsed / IconPopSeconds);
            icon.localScale = iconScale * Mathf.Lerp(IconPopStartScale, 1f, 1f - remaining * remaining);
            yield return null;
        }

        icon.localScale = iconScale;
        popRoutine = null;
    }
}
