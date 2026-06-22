using UnityEngine;
using System.Collections;

public class PulseEffect : MonoBehaviour
{
    [SerializeField] private Transform ring;
    [SerializeField] private SpriteRenderer ringRenderer;
    [SerializeField] private float duration = 1.2f;
    [SerializeField] private float maxScale = 2.5f;

    private bool isPulsing = false;
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
            Debug.LogError("[PulseEffect] Ring transform is missing; pulse animation cannot run.", this);

        if (ringRenderer == null)
            Debug.LogError("[PulseEffect] Ring SpriteRenderer is missing; pulse visuals are unavailable.", this);

        if (!IsFinite(duration) || duration <= 0f)
            Debug.LogWarning("[PulseEffect] duration must be finite and greater than zero.", this);

        if (!IsFinite(maxScale) || maxScale < 0f)
            Debug.LogWarning("[PulseEffect] maxScale must be finite and non-negative.", this);
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
            float safeDuration = duration > 0.01f ? duration : 0.01f;

            while (timer < safeDuration && isPulsing)
            {
                timer += Time.deltaTime;
                float t = timer / safeDuration;

                float scale = Mathf.Lerp(1f, maxScale, t);
                ring.localScale = new Vector3(scale, scale, 1f);
                SetRendererAlpha(Mathf.Lerp(1f, 0f, t));
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
