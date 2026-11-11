using MixedReality.Toolkit;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Runtime layout pass for the icon cells of the filter and spawn menus, plus
// two small menu fixes. It finds its targets by their existing object names
// and edits no prefab or scene, so scene overrides and unpacked copies are
// handled the same way.
//
// Per cell (32 x 32 canvas units):
// - the class icon is drawn whole, in the upper part of the cell;
// - the caption sits below it on the plate instead of across the pin;
// - the checkbox becomes a corner badge instead of covering the glyph;
// - for a real toggle, a switched-off class fades, so the menu shows at a
//   glance what is currently hidden on the map.
[DisallowMultipleComponent]
public class MenuCellStyle : MonoBehaviour
{
    private const string CheckboxName = "Checkbox";
    private const string SpriteIconName = "UIButtonSpriteIcon";
    private const string LabelName = "Label";

    // Sizes, positions and off-state opacity come from TriffidVisualSettings.
    private const float FadePerSecond = 8f;

    private TriffidVisualSettings settings;
    private StatefulInteractable button;
    private Graphic iconGraphic;
    private Graphic labelGraphic;
    private float iconAlpha = 1f;
    private float labelAlpha = 1f;
    private float shown = 1f;
    private bool tracksToggle;

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
        if (!TriffidVisualSettings.Current.menuCellLayout)
            return;

        RectTransform[] rects = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform rect in rects)
        {
            if (rect == null || rect.gameObject.name != CheckboxName)
                continue;

            Transform cell = rect.parent;
            if (cell == null || cell.GetComponent<MenuCellStyle>() != null)
                continue;

            // Only cells that show a class icon; the "All" master has none.
            Transform spriteIcon = cell.Find(SpriteIconName);
            if (spriteIcon == null || !spriteIcon.gameObject.activeSelf || spriteIcon.GetComponent<Image>() == null)
                continue;

            cell.gameObject.AddComponent<MenuCellStyle>().Arrange(rect, spriteIcon as RectTransform, cell.Find(LabelName) as RectTransform);
        }

        PointInfoPanel[] panels = FindObjectsByType<PointInfoPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (PointInfoPanel panel in panels)
            if (panel != null)
                OrderActionBar(panel.transform);
    }

    // Previous and Next stay adjacent and keep their place when Delete is
    // hidden (it is for the UGV); the destructive action no longer sits
    // between the two most used buttons.
    private static void OrderActionBar(Transform panelRoot)
    {
        Transform previous = FindDeep(panelRoot, "BtnPrev");
        Transform next = FindDeep(panelRoot, "BtnNext");
        Transform delete = FindDeep(panelRoot, "BtnDelete");
        if (previous == null || next == null || delete == null)
            return;

        Transform bar = previous.parent;
        if (bar == null || next.parent != bar || delete.parent != bar)
            return;

        int first = Mathf.Min(previous.GetSiblingIndex(), Mathf.Min(next.GetSiblingIndex(), delete.GetSiblingIndex()));
        previous.SetSiblingIndex(first);
        next.SetSiblingIndex(first + 1);
        delete.SetSiblingIndex(first + 2);
    }

    private static Transform FindDeep(Transform root, string objectName)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.gameObject.name == objectName)
                return child;

        return null;
    }

    private void Arrange(RectTransform checkbox, RectTransform spriteIcon, RectTransform label)
    {
        settings = TriffidVisualSettings.Current;

        if (spriteIcon != null)
        {
            spriteIcon.sizeDelta = settings.cellIconSize;
            spriteIcon.anchoredPosition = settings.cellIconPosition;

            Image image = spriteIcon.GetComponent<Image>();
            if (image != null)
            {
                image.preserveAspect = true;
                iconGraphic = image;
                iconAlpha = image.color.a;
            }
        }

        if (label != null)
        {
            label.anchoredPosition = settings.cellCaptionPosition;
            labelGraphic = label.GetComponent<TextMeshProUGUI>();
            if (labelGraphic != null)
                labelAlpha = labelGraphic.color.a;
        }

        if (checkbox != null)
        {
            checkbox.anchoredPosition = settings.cellCheckboxPosition;
            checkbox.localScale = new Vector3(settings.cellCheckboxScale, settings.cellCheckboxScale, 1f);
        }

        button = GetComponentInParent<StatefulInteractable>(true);
        // A plain button (the spawn menu) has no off state to show.
        tracksToggle = button != null && button.ToggleMode != StatefulInteractable.ToggleType.Button;
    }

    private void Update()
    {
        if (!tracksToggle || button == null)
            return;

        float target = button.IsToggled.Active ? 1f : 0f;
        if (Mathf.Approximately(shown, target))
            return;

        shown = Mathf.MoveTowards(shown, target, FadePerSecond * Time.unscaledDeltaTime);
        ApplyFade();
    }

    private void OnEnable()
    {
        // A menu reopened after its toggles changed shows the right state at once.
        if (tracksToggle && button != null)
        {
            shown = button.IsToggled.Active ? 1f : 0f;
            ApplyFade();
        }
    }

    private void ApplyFade()
    {
        if (iconGraphic != null)
        {
            Color color = iconGraphic.color;
            color.a = iconAlpha * Mathf.Lerp(settings.cellOffIconAlpha, 1f, shown);
            iconGraphic.color = color;
        }

        if (labelGraphic != null)
        {
            Color color = labelGraphic.color;
            color.a = labelAlpha * Mathf.Lerp(settings.cellOffCaptionAlpha, 1f, shown);
            labelGraphic.color = color;
        }
    }
}
