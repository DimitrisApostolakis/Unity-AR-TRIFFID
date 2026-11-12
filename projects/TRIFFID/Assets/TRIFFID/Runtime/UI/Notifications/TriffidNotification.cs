using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum NotificationKind
{
    Error,
    Success,
    Warning
}

// A short message card that appears in front of the user and removes itself.
//
//   TriffidNotification.ShowError("Polygon lines cross. Move or undo a vertex.");
//   TriffidNotification.ShowError("Aim at the map to place a point.", "No surface");
//   TriffidNotification.ShowSuccess("Connected to the server.", "Online");
//   TriffidNotification.ShowWarning("Battery is low.", "UGV");
//   TriffidNotification.ShowQuiet(NotificationKind.Warning, "31.5 C, limit 29 C", "Temperature alert");
//
// An error is red with a "!" and shakes as it arrives; a warning is amber with
// a "!"; a success is green with a tick. All use the same card.
//
// A quiet message is for news the user should be able to see without being
// interrupted: the card is smaller, sits low and to the side of the view and
// never shakes.
//
// Only one card is visible at a time. A message that arrives while another
// is shown waits its turn, so two cards never cover each other:
// - messages are shown in the order they arrive, normal ones before quiet ones;
// - a normal message does not wait behind a quiet one: the quiet card steps
//   aside at once and is shown again afterwards;
// - a message identical to the visible one only restarts its timer, and one
//   identical to a waiting message is not added again, so a repeated error
//   never builds up a line of copies.
//
// The card is the prefab "TRIFFID Notification" in a Resources folder. One
// copy per placement is created the first time it is needed and reused
// afterwards. Layout, colours and fonts are edited on the prefab; timing,
// placement and the length of the waiting line are the fields below.
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
    [SerializeField] private Color warningColor = new Color(1.00f, 0.77f, 0.24f);
    [Tooltip("Shown inside the icon disc for an error or a warning.")]
    [SerializeField] private GameObject errorMark;
    [Tooltip("Shown inside the icon disc for a success.")]
    [SerializeField] private GameObject successMark;

    [Header("Waiting Line")]
    [Min(1)]
    [Tooltip("Most messages that may wait behind the visible one. When more arrive, the oldest waiting quiet message is dropped.")]
    [SerializeField] private int maxWaiting = 6;

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

    [Header("Quiet Placement")]
    [Min(0.2f)]
    [Tooltip("Metres in front of the head.")]
    [SerializeField] private float quietDistance = 0.75f;
    [Tooltip("Metres to the right (positive) or left (negative) of the line of sight.")]
    [SerializeField] private float quietSideOffset = 0.2f;
    [Tooltip("Metres above (positive) or below (negative) the line of sight.")]
    [SerializeField] private float quietHeightOffset = -0.2f;
    [Range(0.2f, 1f)]
    [Tooltip("Size of a quiet card relative to a normal one.")]
    [SerializeField] private float quietScale = 0.7f;
    [Min(0f)]
    [Tooltip("How quickly a quiet card follows the head. Lower drifts more gently.")]
    [SerializeField] private float quietFollowSpeed = 2.5f;
    [Min(0.5f)]
    [Tooltip("Shortest time a quiet message stays; it is off to the side, so it gets longer than a normal one.")]
    [SerializeField] private float quietMinimumSeconds = 6f;

    [Header("Motion")]
    [Tooltip("Sideways shake of the card when an error arrives, in canvas units.")]
    [SerializeField] private float shakeAmount = 5f;
    [Tooltip("Canvas units the card drifts upward while it vanishes.")]
    [SerializeField] private float vanishRise = 10f;

    private sealed class Request
    {
        public NotificationKind kind;
        public string title;
        public string message;
        public bool quiet;

        public bool SameAs(Request other)
        {
            return other != null && kind == other.kind && quiet == other.quiet && title == other.title && message == other.message;
        }
    }

    // One card per placement: [0] in the line of sight, [1] quiet.
    private static readonly TriffidNotification[] instances = new TriffidNotification[2];
    // Messages not shown yet: normal ones first, then quiet ones, each in arrival order.
    private static readonly List<Request> waiting = new List<Request>();
    private static Request showing;
    private static TriffidNotification showingCard;
    private static bool missingPrefabLogged;

    private Coroutine routine;
    private float shakeNow;
    private Vector2 cardRestPosition;
    private bool restPositionKnown;
    private bool quiet;

    // Play sessions in the Editor can keep statics alive; start each one fresh.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        waiting.Clear();
        showing = null;
        showingCard = null;
    }

    // All return false only when the prefab is missing, so a caller can fall back.
    public static bool ShowError(string message, string title = "Error")
    {
        return Show(NotificationKind.Error, message, title, false);
    }

    public static bool ShowSuccess(string message, string title = "Done")
    {
        return Show(NotificationKind.Success, message, title, false);
    }

    public static bool ShowWarning(string message, string title = "Warning")
    {
        return Show(NotificationKind.Warning, message, title, false);
    }

    // Small, to the side, no shake. See the notes at the top of this file.
    public static bool ShowQuiet(NotificationKind kind, string message, string title = null)
    {
        return Show(kind, message, title, true);
    }

    public static bool Show(NotificationKind kind, string message, string title, bool quiet)
    {
        if (string.IsNullOrWhiteSpace(message) || !Application.isPlaying)
            return false;

        TriffidNotification card = CardFor(quiet);
        if (card == null)
            return false;

        Request request = new Request
        {
            kind = kind,
            title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            message = message.Trim(),
            quiet = quiet
        };

        // A card hidden from outside leaves nothing on show.
        bool busy = showingCard != null && showingCard.routine != null;
        if (!busy)
        {
            showing = null;
            showingCard = null;
        }

        if (busy && request.SameAs(showing))
        {
            showingCard.Present(request);
            return true;
        }

        foreach (Request other in waiting)
            if (request.SameAs(other))
                return true;

        if (busy && !request.quiet && showing.quiet)
        {
            // The quiet card steps aside and comes back after the normal ones.
            Request interrupted = showing;
            showingCard.HideNow();
            AddToWaiting(request, card.maxWaiting);
            waiting.Insert(NormalWaitingCount(), interrupted);
            ShowNext();
            return true;
        }

        AddToWaiting(request, card.maxWaiting);
        if (!busy)
            ShowNext();
        return true;
    }

    private static int NormalWaitingCount()
    {
        int count = 0;
        while (count < waiting.Count && !waiting[count].quiet)
            count++;
        return count;
    }

    private static void AddToWaiting(Request request, int limit)
    {
        if (request.quiet)
            waiting.Add(request);
        else
            waiting.Insert(NormalWaitingCount(), request);

        // Too many: the oldest quiet message goes first, then the oldest of all.
        while (waiting.Count > Mathf.Max(1, limit))
        {
            int normal = NormalWaitingCount();
            waiting.RemoveAt(normal < waiting.Count ? normal : 0);
        }
    }

    private static void ShowNext()
    {
        showing = null;
        showingCard = null;

        while (waiting.Count > 0)
        {
            Request next = waiting[0];
            waiting.RemoveAt(0);

            TriffidNotification card = CardFor(next.quiet);
            if (card == null)
                continue;

            showing = next;
            showingCard = card;
            card.Present(next);
            return;
        }
    }

    // The card of one placement, created on first use. Null when the prefab is missing.
    private static TriffidNotification CardFor(bool quiet)
    {
        int slot = quiet ? 1 : 0;
        TriffidNotification instance = instances[slot];
        if (instance != null)
            return instance;

        TriffidNotification prefab = Resources.Load<TriffidNotification>(ResourceName);
        if (prefab == null)
        {
            if (!missingPrefabLogged)
            {
                missingPrefabLogged = true;
                TriffidLog.Warning("TriffidNotification", "Prefab '" + ResourceName + "' was not found in a Resources folder; notifications are not shown.");
            }
            return null;
        }

        // Created hidden, so a card that has to wait never flashes up.
        instance = Instantiate(prefab);
        instance.name = quiet ? ResourceName + " (Quiet)" : ResourceName;
        instance.quiet = quiet;
        if (quiet)
            instance.transform.localScale *= instance.quietScale;
        instance.gameObject.SetActive(false);
        instances[slot] = instance;
        return instance;
    }

    // Removes the card without its vanish animation.
    private void HideNow()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
        gameObject.SetActive(false);
    }

    private Color ColorOf(NotificationKind kind)
    {
        switch (kind)
        {
            case NotificationKind.Success: return successColor;
            case NotificationKind.Warning: return warningColor;
            default: return errorColor;
        }
    }

    private static string DefaultTitle(NotificationKind kind)
    {
        switch (kind)
        {
            case NotificationKind.Success: return "DONE";
            case NotificationKind.Warning: return "WARNING";
            default: return "ERROR";
        }
    }

    private void Present(Request request)
    {
        NotificationKind kind = request.kind;
        string title = request.title;
        string message = request.message;

        bool success = kind == NotificationKind.Success;
        Color kindColor = ColorOf(kind);
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
        shakeNow = kind == NotificationKind.Error && !quiet ? shakeAmount : 0f;

        if (titleText != null) titleText.text = title == null ? DefaultTitle(kind) : title.ToUpperInvariant();
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
        float shortest = quiet ? quietMinimumSeconds : minimumSeconds;
        float seconds = Mathf.Clamp(shortest + message.Length * secondsPerCharacter, shortest, Mathf.Max(shortest, maximumSeconds));
        // A quiet card slides in without the pop of a card that wants attention.
        routine = StartCoroutine(Run(seconds, alreadyVisible || quiet, alreadyVisible));
    }

    private IEnumerator Run(float visibleSeconds, bool gentle, bool alreadyVisible)
    {
        float startAlpha = alreadyVisible && canvasGroup != null ? canvasGroup.alpha : 0f;
        SetTimer(1f);

        // Appear: fade in, grow past full size and settle, with a short damped shake.
        for (float t = 0f; t < appearSeconds; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / appearSeconds);
            SetAlpha(Mathf.Lerp(startAlpha, 1f, k));
            SetCard(Mathf.LerpUnclamped(gentle ? 0.94f : 0.8f, 1f, EaseOutBack(k)),
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

        if (showingCard == this)
            ShowNext();
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
        for (int i = 0; i < instances.Length; i++)
            if (instances[i] == this) instances[i] = null;
        if (showingCard == this)
        {
            showing = null;
            showingCard = null;
        }
    }

    private void PlaceInFrontOfHead(bool snap)
    {
        Camera head = Camera.main;
        if (head == null) return;

        Transform eye = head.transform;
        Vector3 target = quiet
            ? eye.position + eye.forward * quietDistance + eye.right * quietSideOffset + eye.up * quietHeightOffset
            : eye.position + eye.forward * distance + eye.up * heightOffset;
        float speed = quiet ? quietFollowSpeed : followSpeed;
        if (snap)
            transform.position = target;
        else if (speed > 0f)
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));

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
