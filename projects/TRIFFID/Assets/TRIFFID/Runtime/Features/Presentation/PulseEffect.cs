using UnityEngine;
using System.Collections;

public class PulseEffect : MonoBehaviour
{
    [SerializeField] private Transform ring;
    [SerializeField] private SpriteRenderer ringRenderer;
    [SerializeField] private float duration = 1.2f;
    [SerializeField] private float maxScale = 2.5f;

    private bool isPulsing = false;

    // Read by MarkerFootprint so the ground ring emphasises the same marker.
    public bool IsPulsing => isPulsing;

    // The halo takes the marker's group colour; its opacity is animated separately.
    public void SetTint(Color tint)
    {
        if (ringRenderer == null)
            return;

        tint.a = ringRenderer.color.a;
        ringRenderer.color = tint;
    }

    // Keeps the ring on the drawn icon when the sprite pivot is not its centre.
    public void CenterOn(Vector3 worldPosition)
    {
        if (ring != null)
            ring.position = worldPosition;
    }
    private Coroutine pulseRoutine;
    private MaterialPropertyBlock mpb;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    private void Awake()
    {
        mpb = new MaterialPropertyBlock();
        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (ring == null)
            TriffidLog.Error("PulseEffect", "Ring transform is missing; pulse animation cannot run.", this);

        if (ringRenderer == null)
            TriffidLog.Error("PulseEffect", "Ring SpriteRenderer is missing; pulse visuals are unavailable.", this);

        if (!IsFinite(duration) || duration <= 0f)
            TriffidLog.Warning("PulseEffect", "duration must be finite and greater than zero.", this);

        if (!IsFinite(maxScale) || maxScale < 0f)
            TriffidLog.Warning("PulseEffect", "maxScale must be finite and non-negative.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [ContextMenu("Test Pulse")]
    public void StartPulse()
    {
        if (ring == null || ringRenderer == null)
            return;

        // A marker hidden by a category filter cannot run the animation. Starting
        // it anyway logged an error and left the pulse flagged as running.
        if (!isActiveAndEnabled)
            return;

        isPulsing = true;
        ring.localScale = Vector3.one;
        SetRendererAlpha(1f);

        if (pulseRoutine != null)
            StopCoroutine(pulseRoutine);

        pulseRoutine = StartCoroutine(PulseLoop());
    }

    public void StopPulse()
    {
        isPulsing = false;
        if (pulseRoutine != null)
        {
            StopCoroutine(pulseRoutine);
            pulseRoutine = null;
        }

        if (ring == null || ringRenderer == null)
            return;

        ring.localScale = Vector3.zero;
        SetRendererAlpha(0f);
    }

    private IEnumerator PulseLoop()
    {
        while (isPulsing)
        {
            float timer = 0f;
            TriffidVisualSettings settings = TriffidVisualSettings.Current;
            float cycleSeconds = settings.overrideHalo ? settings.haloSeconds : duration;
            float cycleScale = settings.overrideHalo ? settings.haloMaxScale : maxScale;
            float safeDuration = cycleSeconds > 0.01f ? cycleSeconds : 0.01f;

            while (timer < safeDuration && isPulsing)
            {
                timer += Time.deltaTime;
                float t = Mathf.Clamp01(timer / safeDuration);

                // Ease out: the ring opens quickly, then settles and fades,
                // instead of travelling at a constant, mechanical speed.
                float remaining = 1f - t;
                float scale = Mathf.Lerp(1f, cycleScale, 1f - remaining * remaining);
                ring.localScale = new Vector3(scale, scale, 1f);
                SetRendererAlpha(remaining * remaining);
                yield return null;
            }

            if (!isPulsing)
                yield break;

            ring.localScale = Vector3.one;
            SetRendererAlpha(1f);
        }

        pulseRoutine = null;
    }

    private void SetRendererAlpha(float alpha)
    {
        if (ringRenderer == null)
            return;

        // A soft halo rather than a solid disc; the crisp cue is the ground wave.
        alpha *= TriffidVisualSettings.Current.pulseHaloAlpha;

        // OnDisable can run on an object whose Awake never did (destroyed while inactive).
        if (mpb == null)
            mpb = new MaterialPropertyBlock();

        ringRenderer.GetPropertyBlock(mpb);
        Color c = ringRenderer.color;
        c.a = alpha;
        mpb.SetColor(ColorProperty, c);
        ringRenderer.SetPropertyBlock(mpb);
    }

    private void OnDisable()
    {
        StopPulse();
    }
}
