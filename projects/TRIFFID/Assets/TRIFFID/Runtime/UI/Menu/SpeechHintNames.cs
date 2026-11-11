using MixedReality.Toolkit.UX;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Every button left on MRTK's generic keyword shows the hint "Say 'select'".
// This gives such a button its own caption as keyword, so the hint reads
// "Say 'Fire'" and that word triggers that button. Buttons that already have
// a specific keyword are left alone. No prefab or scene is edited.
public static class SpeechHintNames
{
    private const string GenericKeyword = "select";
    private const string LabelName = "Label";
    private const string TextName = "Text";
    private const string HintRootName = "SeeItSayItLabel-Canvas";

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
        if (!TriffidVisualSettings.Current.nameSpeechHints)
            return;

        PressableButton[] buttons = Object.FindObjectsByType<PressableButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (PressableButton button in buttons)
        {
            if (button == null)
                continue;

            string keyword = button.SpeechRecognitionKeyword;
            if (!string.IsNullOrWhiteSpace(keyword) &&
                !string.Equals(keyword.Trim(), GenericKeyword, System.StringComparison.OrdinalIgnoreCase))
                continue;

            string caption = FindCaption(button.transform);
            if (string.IsNullOrEmpty(caption))
                continue;

            // MRTK builds the hint from this keyword when the button starts.
            button.SpeechRecognitionKeyword = caption;

            // A button that has already started keeps its old hint text; update it too.
            Transform hintRoot = button.transform.Find(HintRootName);
            if (hintRoot == null)
                continue;

            TMP_Text hint = hintRoot.GetComponentInChildren<TMP_Text>(true);
            if (hint != null)
                hint.text = "Say '" + caption + "'";
        }
    }

    // The visible caption: the button's own Label or Text, without markup.
    private static string FindCaption(Transform button)
    {
        string fallback = null;
        foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text == null)
                continue;

            string objectName = text.gameObject.name;
            if (objectName != LabelName && objectName != TextName)
                continue;

            // The hint's own text object is also called Label.
            if (IsInsideHint(text.transform, button))
                continue;

            string caption = StripMarkup(text.text);
            if (string.IsNullOrEmpty(caption) || caption == "Label" || caption == "Button")
                continue;

            if (text.gameObject.activeSelf)
                return caption;

            if (fallback == null)
                fallback = caption;
        }

        return fallback;
    }

    private static bool IsInsideHint(Transform node, Transform button)
    {
        for (Transform current = node; current != null && current != button; current = current.parent)
            if (current.gameObject.name == HintRootName)
                return true;

        return false;
    }

    private static string StripMarkup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        System.Text.StringBuilder plain = new System.Text.StringBuilder(value.Length);
        bool insideTag = false;
        foreach (char character in value)
        {
            if (character == '<') insideTag = true;
            else if (character == '>') insideTag = false;
            else if (!insideTag) plain.Append(character);
        }

        return plain.ToString().Trim();
    }
}
