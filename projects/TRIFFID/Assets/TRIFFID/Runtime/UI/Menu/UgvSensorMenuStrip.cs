using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// A row at the top of the main menu with one cell per UGV reading:
//
//   | TEMPERATURE      | CO2              |
//   | 31.5 °C          | OK               |
//   | limit 29.0 · now | below 600 ppm    |
//
// The colour of a cell is the level of its reading (UgvSensorLevel):
// green, no alert on record; red, the limit was reached moments ago; amber,
// it was reached earlier, with the age of that alert; grey, no data yet.
// A cell glows briefly when a new alert for it arrives.
//
// Like MenuCellStyle it attaches itself at runtime and finds the menu by
// object name, so no prefab or scene is edited. It makes room by adding the
// strip height to the top padding of the menu's own layout group; the menu's
// content size fitter and backplate then grow with it. Sizes, colours and the
// menu object name come from UgvSensorSettings; the values come from
// UgvSensorMonitor.
[DisallowMultipleComponent]
public class UgvSensorMenuStrip : MonoBehaviour
{
    private const string StripName = "UGV Sensors";
    private const float AgeRefreshSeconds = 1f;
    private const float GlowPeakAlpha = 0.3f;

    private sealed class Cell
    {
        public UgvSensorDefinition definition;
        public Image accent;
        public Image glow;
        public TextMeshProUGUI caption;
        public TextMeshProUGUI value;
        public TextMeshProUGUI detail;
        public float glowLeft;
    }

    private readonly List<Cell> cells = new List<Cell>();
    private UgvSensorSettings settings;
    private float nextRefresh;

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
        UgvSensorSettings settings = UgvSensorSettings.Current;
        if (!settings.sensorsEnabled || !settings.showMenuStrip || settings.sensors.Count == 0)
            return;

        RectTransform[] rects = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform rect in rects)
        {
            if (rect == null || rect.gameObject.name != settings.menuContainerName || rect.GetComponent<UgvSensorMenuStrip>() != null)
                continue;

            HorizontalOrVerticalLayoutGroup layout = rect.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (layout == null)
            {
                TriffidLog.Warning("UgvSensorMenuStrip", "'" + rect.gameObject.name + "' has no layout group; the UGV readings are not shown on it.", rect);
                continue;
            }

            rect.gameObject.AddComponent<UgvSensorMenuStrip>().Build(rect, layout, settings);
        }
    }

    private void Build(RectTransform menu, HorizontalOrVerticalLayoutGroup layout, UgvSensorSettings useSettings)
    {
        settings = useSettings;

        RectOffset padding = layout.padding;
        int originalTop = padding.top;
        int room = Mathf.CeilToInt(settings.stripHeight + settings.stripGap);
        MakeRoom(menu, layout, room);

        RectTransform strip = NewRect(StripName, menu);
        strip.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        strip.anchorMin = new Vector2(0f, 1f);
        strip.anchorMax = new Vector2(1f, 1f);
        strip.pivot = new Vector2(0.5f, 1f);
        strip.offsetMin = new Vector2(padding.left, -originalTop - settings.stripHeight);
        strip.offsetMax = new Vector2(-padding.right, -originalTop);
        // In front of the backplate, like the menu's own labels; at the same
        // depth the two flicker through each other.
        Vector3 local = strip.localPosition;
        local.z = settings.stripDepthOffset;
        strip.localPosition = local;

        TMP_FontAsset font = ChooseFont(menu, strip);

        List<UgvSensorDefinition> sensors = settings.sensors;
        int count = sensors.Count;
        float halfSpace = settings.cellSpacing * 0.5f;
        for (int i = 0; i < count; i++)
        {
            if (sensors[i] == null)
                continue;

            RectTransform cellRect = NewRect(sensors[i].key, strip);
            cellRect.anchorMin = new Vector2((float)i / count, 0f);
            cellRect.anchorMax = new Vector2((float)(i + 1) / count, 1f);
            cellRect.offsetMin = new Vector2(i == 0 ? 0f : halfSpace, 0f);
            cellRect.offsetMax = new Vector2(i == count - 1 ? 0f : -halfSpace, 0f);
            cells.Add(BuildCell(cellRect, sensors[i], font));
        }

        Refresh();
    }

    // Adds the strip height above the menu's buttons. The layout group and
    // the content size fitter do the rest; a menu of fixed size is enlarged.
    private void MakeRoom(RectTransform menu, HorizontalOrVerticalLayoutGroup layout, int room)
    {
        RectOffset padding = layout.padding;
        layout.padding = new RectOffset(padding.left, padding.right, padding.top + room, padding.bottom);

        ContentSizeFitter fitter = menu.GetComponent<ContentSizeFitter>();
        bool fitsHeight = fitter != null && fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained;
        if (!fitsHeight)
            menu.sizeDelta += new Vector2(0f, room);

        // The menu grows around its pivot; lift it so the bottom edge, and
        // the grab bar under it, stay where they were.
        if (settings.keepMenuBottomEdge)
            menu.anchoredPosition += new Vector2(0f, room * menu.pivot.y);

        LayoutRebuilder.MarkLayoutForRebuild(menu);
    }

    private Cell BuildCell(RectTransform cellRect, UgvSensorDefinition definition, TMP_FontAsset font)
    {
        Cell cell = new Cell { definition = definition };

        Image plate = cellRect.gameObject.AddComponent<Image>();
        plate.color = settings.cellPlateColor;
        plate.raycastTarget = false;

        cell.glow = NewImage("Glow", cellRect);
        Stretch(cell.glow.rectTransform, 0f, 0f, 1f, 1f);
        SetAlpha(cell.glow, 0f);

        cell.accent = NewImage("Level", cellRect);
        RectTransform accent = cell.accent.rectTransform;
        accent.anchorMin = new Vector2(0f, 0f);
        accent.anchorMax = new Vector2(0f, 1f);
        accent.pivot = new Vector2(0f, 0.5f);
        accent.sizeDelta = new Vector2(settings.cellAccentWidth, 0f);
        accent.anchoredPosition = Vector2.zero;

        cell.caption = NewText("Caption", cellRect, font, settings.captionFontSize, 0.68f, 1f);
        cell.value = NewText("Value", cellRect, font, settings.valueFontSize, 0.27f, 0.72f);
        cell.detail = NewText("Detail", cellRect, font, settings.detailFontSize, 0f, 0.3f);

        cell.caption.text = UgvSensorText.Name(definition, definition.key).ToUpperInvariant();
        cell.value.fontStyle = FontStyles.Bold;
        return cell;
    }

    private void OnEnable()
    {
        UgvSensorMonitor.Changed -= Refresh;
        UgvSensorMonitor.Changed += Refresh;
        UgvSensorMonitor.AlertsArrived -= OnAlertsArrived;
        UgvSensorMonitor.AlertsArrived += OnAlertsArrived;

        // A menu reopened after a while shows current values and ages at once.
        if (settings != null)
            Refresh();
    }

    private void OnDisable()
    {
        UgvSensorMonitor.Changed -= Refresh;
        UgvSensorMonitor.AlertsArrived -= OnAlertsArrived;
    }

    private void Update()
    {
        // Ages and the Alert level change with time alone.
        if (Time.unscaledTime >= nextRefresh)
            Refresh();

        foreach (Cell cell in cells)
        {
            if (cell.glowLeft <= 0f)
                continue;

            cell.glowLeft = Mathf.Max(0f, cell.glowLeft - Time.unscaledDeltaTime);
            SetAlpha(cell.glow, GlowPeakAlpha * cell.glowLeft / Mathf.Max(0.01f, settings.newAlertGlowSeconds));
        }
    }

    private void OnAlertsArrived(UgvSensorAlertBatch batch)
    {
        if (batch == null)
            return;

        foreach (UgvSensorAlert alert in batch.alerts)
            foreach (Cell cell in cells)
                if (string.Equals(cell.definition.key, alert.sensor, StringComparison.OrdinalIgnoreCase))
                    cell.glowLeft = settings.newAlertGlowSeconds;
    }

    private void Refresh()
    {
        nextRefresh = Time.unscaledTime + AgeRefreshSeconds;

        UgvSensorMonitor monitor = UgvSensorMonitor.Instance;
        DateTime now = DateTime.UtcNow;

        foreach (Cell cell in cells)
        {
            UgvSensorState state = monitor != null ? monitor.StateOf(cell.definition.key) : null;
            UgvSensorLevel level = state != null ? state.LevelAt(now, settings, monitor.DataReceived) : UgvSensorLevel.Unknown;
            Color color = settings.ColorOf(level);

            cell.accent.color = color;
            cell.value.color = color;
            Color glow = color;
            glow.a = cell.glow.color.a;
            cell.glow.color = glow;

            cell.value.text = ValueText(cell.definition, state, level);
            cell.detail.text = DetailText(cell.definition, state, level, now);
        }
    }

    private static string ValueText(UgvSensorDefinition definition, UgvSensorState state, UgvSensorLevel level)
    {
        switch (level)
        {
            case UgvSensorLevel.Alert:
            case UgvSensorLevel.Earlier:
                return UgvSensorText.Value(definition, state.lastAlert.value);
            case UgvSensorLevel.Normal:
                return "OK";
            default:
                return "--";
        }
    }

    private static string DetailText(UgvSensorDefinition definition, UgvSensorState state, UgvSensorLevel level, DateTime now)
    {
        if (level == UgvSensorLevel.Unknown)
            return "no data";

        if (level == UgvSensorLevel.Normal)
            return state.hasThreshold ? "below " + UgvSensorText.Value(definition, state.threshold) : "no alert";

        // The limit that was in force when the alert was recorded.
        UgvSensorAlert alert = state.lastAlert;
        string limit = "limit " + UgvSensorText.Value(definition, alert.threshold, false);
        return alert.hasTimestamp ? limit + " · " + UgvSensorText.Age(alert.timestampUtc, now) : limit;
    }

    // ---- construction helpers ----

    private RectTransform NewRect(string objectName, Transform parent)
    {
        GameObject created = new GameObject(objectName, typeof(RectTransform));
        created.layer = gameObject.layer;
        RectTransform rect = (RectTransform)created.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private Image NewImage(string objectName, Transform parent)
    {
        Image image = NewRect(objectName, parent).gameObject.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    // A single line that spans the cell between two heights (0 bottom, 1 top).
    private TextMeshProUGUI NewText(string objectName, Transform parent, TMP_FontAsset font, float size, float bottom, float top)
    {
        TextMeshProUGUI text = NewRect(objectName, parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
            text.font = font;

        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        // Long values shrink to stay inside the cell instead of being cut.
        text.enableAutoSizing = true;
        text.fontSizeMax = size;
        text.fontSizeMin = Mathf.Min(size, Mathf.Max(1f, size * 0.55f));
        text.color = new Color(1f, 1f, 1f, settings.secondaryTextAlpha);

        Stretch(text.rectTransform, 0f, bottom, 1f, top);
        float left = settings.cellAccentWidth + 2.5f;
        text.rectTransform.offsetMin = new Vector2(left, 0f);
        text.rectTransform.offsetMax = new Vector2(-1.5f, 0f);
        return text;
    }

    // The typeface of the menu itself, when it can draw every unit sign the
    // strip needs (the degree sign is not in every font); otherwise the
    // TextMesh Pro default font, which is used as it is.
    private TMP_FontAsset ChooseFont(RectTransform menu, RectTransform strip)
    {
        string needed = "\u00B7";
        foreach (UgvSensorDefinition sensor in settings.sensors)
            if (sensor != null)
                needed += sensor.unit;

        foreach (TextMeshProUGUI label in menu.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (label == null || label.font == null || label.transform.IsChildOf(strip))
                continue;

            // Icon glyphs sit in the private-use range; skip icon-only labels.
            string text = label.text;
            if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] >= '\uE000')
                continue;

            return label.font.HasCharacters(needed, out _, true, true) ? label.font : null;
        }
        return null;
    }

    private static void Stretch(RectTransform rect, float minX, float minY, float maxX, float maxY)
    {
        rect.anchorMin = new Vector2(minX, minY);
        rect.anchorMax = new Vector2(maxX, maxY);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void SetAlpha(Graphic graphic, float alpha)
    {
        Color color = graphic.color;
        color.a = alpha;
        graphic.color = color;
    }
}
