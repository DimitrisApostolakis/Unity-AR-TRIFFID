using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// A short message card that appears in front of the user and removes itself.
//
//   TriffidNotification.ShowError("Polygon lines cross. Move or undo a vertex.");
//   TriffidNotification.ShowError("Aim at the map to place a point.", "No surface");
//   TriffidNotification.ShowSuccess("Connected to the server.", "Online");
//
// An error is red with a "!" and shakes as it arrives; a success is green with
// a tick and does not shake. Both use the same card.
//
// The card is the prefab "TRIFFID Notification" in a Resources folder. It is
// created the first time a message is shown and reused afterwards; a new
// message while one is visible replaces the text and restarts the timer.
// Layout, colours and fonts are edited on the prefab; timing and placement are
// the fields below.
[DisallowMultipleComponent]
public class TriffidNotification : MonoBehaviour
{
    public const string ResourceName = "TRIFFID Notification";

    [Header("Parts")]
    [SerializeField] private CanvasGroup canvasGroup;
    [Tooltip("Scaled and nudged by the animations.")]
    [SerializeField] private RectTransform card;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text messageText;
    [Tooltip("Shrinks from full width to nothing while the card is visible.")]
    [SerializeField] private RectTransform timerFill;
    [Tooltip("Pulses once when a message arrives.")]
    [SerializeField] private RectTransform icon;

    [Header("Colours")]
    [Tooltip("Graphics that take the colour of the message kind: accent strip, icon disc, timer bar and title.")]
    [SerializeField] private Graphic[] tinted;
    [SerializeField] private Color errorColor = new Color(1f, 0.33f, 0.33f);
    [SerializeField] private Color successColor = new Color(0.29f, 0.87f, 0.50f);
    [Tooltip("Shown inside the icon disc for an error.")]
    [SerializeField] private GameObject errorMark;
    [Tooltip("Shown inside the icon disc for a success.")]
    [SerializeField] private GameObject successMark;

    [Header("Timing (seconds)")]
    [Min(0.5f)]
    [Tooltip("Shortest time a message stays readable.")]
    [SerializeField] private float minimumSeconds = 3f;
    [Min(0.5f)]
    [Tooltip("Longest time a message stays, however long its text.")]
    [SerializeField] private float maximumSeconds = 7f;
    [Min(0f)]
    [Tooltip("Extra reading time per character of the message.")]
    [SerializeField] private float secondsPerCharacter = 0.045f;
    [Min(0.05f)]
    [SerializeField] private float appearSeconds = 0.28f;
    [Min(0.05f)]
    [SerializeField] private float vanishSeconds = 0.4f;

    [Header("Placement")]
    [Min(0.2f)]
    [Tooltip("Metres in front of the head.")]
    [SerializeField] private float distance = 0.6f;
    [Tooltip("Metres above (positive) or below (negative) the line of sight.")]
    [SerializeField] private float heightOffset = -0.13f;
    [Min(0f)]
    [Tooltip("How quickly the card follows the head. Zero keeps it where it appeared.")]
    [SerializeField] private float followSpeed = 5f;

    [Header("Motion")]
    [Tooltip("Sideways shake of the card when an error arrives, in canvas units.")]
    [SerializeField] private float shakeAmount = 5f;
    [Tooltip("Canvas units the card drifts upward while it vanishes.")]
    [SerializeField] private float vanishRise = 10f;

    private static TriffidNotification instance;
    private static bool missingPrefabLogged;

    private Coroutine routine;
    private float shakeNow;
    private Vector2 cardRestPosition;
    private bool restPositionKnown;

    // Both return false only when the prefab is missing, so a caller can fall back.
    public static bool ShowError(string message, string title = "Error")
    {
        return Show(message, title, false);
    }

    public static bool ShowSuccess(string message, string title = "Done")
    {
        return Show(message, title, true);
    }

    private static bool Show(string message, string title, bool success)
    {
        if (string.IsNullOrWhiteSpace(message) || !Application.isPlaying)
            return false;

        if (instance == null)
        {
            TriffidNotification prefab = Resources.Load<TriffidNotification>(ResourceName);
            if (prefab == null)
            {
                if (!missingPrefabLogged)
                {
                    missingPrefabLogged = true;
                    TriffidLog.Warning("TriffidNotification", "Prefab '" + ResourceName + "' was not found in a Resources folder; notifications are not shown.");
                }
                return false;
            }

            instance = Instantiate(prefab);
            instance.name = ResourceName;
        }

        instance.Present(title, message.Trim(), success);
        return true;
    }

    private void Present(string title, string message, bool success)
    {
        Color kindColor = success ? successColor : errorColor;
        if (tinted != null)
        {
            foreach (Graphic graphic in tinted)
            {
                if (graphic == null) continue;
                kindColor.a = graphic.color.a;
                graphic.color = kindColor;
            }
        }
        if (errorMark != null) errorMark.SetActive(!success);
        if (successMark != null) successMark.SetActive(success);
        shakeNow = success ? 0f : shakeAmount;

        if (titleText != null) titleText.text = string.IsNullOrWhiteSpace(title) ? (success ? "DONE" : "ERROR") : title.Trim().ToUpperInvariant();
        if (messageText != null) messageText.text = message;

        if (card != null && !restPositionKnown)
        {
            cardRestPosition = card.anchoredPosition;
            restPositionKnown = true;
        }

        bool alreadyVisible = gameObject.activeSelf && routine != null;
        gameObject.SetActive(true);
        if (!alreadyVisible)
            PlaceInFrontOfHead(true);

        if (routine != null) StopCoroutine(routine);
        float seconds = Mathf.Clamp(minimumSeconds + message.Length * secondsPerCharacter, minimumSeconds, Mathf.Max(minimumSeconds, maximumSeconds));
        routine = StartCoroutine(Run(seconds, alreadyVisible));
    }

    private IEnumerator Run(float visibleSeconds, bool alreadyVisible)
    {
        float startAlpha = alreadyVisible && canvasGroup != null ? canvasGroup.alpha : 0f;
        SetTimer(1f);

        // Appear: fade in, grow past full size and settle, with a short damped shake.
        for (float t = 0f; t < appearSeconds; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / appearSeconds);
            SetAlpha(Mathf.Lerp(startAlpha, 1f, k));
            SetCard(Mathf.LerpUnclamped(alreadyVisible ? 0.94f : 0.8f, 1f, EaseOutBack(k)),
                new Vector2(Mathf.Sin(k * Mathf.PI * 5f) * shakeNow * (1f - k), 0f));
            SetIcon(1f + 0.35f * Mathf.Sin(k * Mathf.PI));
            yield return null;
        }
        SetAlpha(1f);
        SetCard(1f, Vector2.zero);
        SetIcon(1f);

        // Stay: the bar drains so the user can see how long is left.
        for (float t = 0f; t < visibleSeconds; t += Time.unscaledDeltaTime)
        {
            SetTimer(1f - t / visibleSeconds);
            yield return null;
        }
        SetTimer(0f);

        // Vanish: fade out while drifting up and shrinking slightly.
        for (float t = 0f; t < vanishSeconds; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / vanishSeconds);
            float eased = k * k;
            SetAlpha(1f - eased);
            SetCard(Mathf.Lerp(1f, 0.92f, eased), new Vector2(0f, vanishRise * eased));
            yield return null;
        }

        SetAlpha(0f);
        SetCard(1f, Vector2.zero);
        routine = null;
        gameObject.SetActive(false);
    }

    private void LateUpdate()
    {
        PlaceInFrontOfHead(false);
    }

    private void OnDisable()
    {
        // Deactivated from outside mid-animation: leave a clean state for the next message.
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
        SetAlpha(0f);
        if (restPositionKnown) SetCard(1f, Vector2.zero);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void PlaceInFrontOfHead(bool snap)
    {
        Camera head = Camera.main;
        if (head == null) return;

        Transform eye = head.transform;
        Vector3 target = eye.position + eye.forward * distance + eye.up * heightOffset;
        if (snap)
            transform.position = target;
        else if (followSpeed > 0f)
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime));

        // A world-space canvas is readable from behind its forward axis.
        Vector3 away = transform.position - eye.position;
        if (away.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(away, eye.up);
    }

    private void SetAlpha(float alpha)
    {
        if (canvasGroup != null) canvasGroup.alpha = alpha;
    }

    private void SetCard(float scale, Vector2 offset)
    {
        if (card == null) return;
        card.localScale = new Vector3(scale, scale, 1f);
        if (restPositionKnown) card.anchoredPosition = cardRestPosition + offset;
    }

    private void SetIcon(float scale)
    {
        if (icon != null) icon.localScale = new Vector3(scale, scale, 1f);
    }

    private void SetTimer(float fraction)
    {
        if (timerFill == null) return;
        Vector2 max = timerFill.anchorMax;
        max.x = Mathf.Clamp01(fraction);
        timerFill.anchorMax = max;
    }

    private static float EaseOutBack(float k)
    {
        const float overshoot = 1.70158f;
        float p = k - 1f;
        return 1f + (overshoot + 1f) * p * p * p + overshoot * p * p;
    }
}
