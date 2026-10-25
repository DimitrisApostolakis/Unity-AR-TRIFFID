using System.Collections;
using UnityEngine;

public class LineWidthController : MonoBehaviour
{
    [Header("Line Renderers")]
    [SerializeField] private LineRenderer[] lines;
    [SerializeField] private bool autoFindInChildren = true;

    [Header("Width Settings")]
    [SerializeField] private float normalLineWidth = 0.01f;
    [SerializeField] private float wideLineWidth   = 0.05f;

    [Header("Animation")]
    [SerializeField] private bool  animateTransition = true;
    [SerializeField] private float widthLerpSpeed    = 5f;

    private bool      isWide = false;
    private Coroutine animCoroutine;

    private void Start()
    {
        if (autoFindInChildren)
            lines = GetComponentsInChildren<LineRenderer>();

        ValidateConfiguration();
        SetLineWidth(normalLineWidth);
    }

    private void ValidateConfiguration()
    {
        if (!HasAnyValidLine())
            Debug.LogWarning("[LineWidthController] No runtime lines found to update.", this);

        if (!IsFinite(normalLineWidth) || normalLineWidth < 0f)
            Debug.LogWarning("[LineWidthController] normalLineWidth must be finite and non-negative.", this);

        if (!IsFinite(wideLineWidth) || wideLineWidth < 0f)
            Debug.LogWarning("[LineWidthController] wideLineWidth must be finite and non-negative.", this);

        if (animateTransition && (!IsFinite(widthLerpSpeed) || widthLerpSpeed <= 0f))
            Debug.LogWarning("[LineWidthController] widthLerpSpeed must be finite and greater than zero while animation is enabled.", this);
    }

    public void ToggleLineWidth()
    {
        isWide = !isWide;
        float targetWidth = isWide ? wideLineWidth : normalLineWidth;

        if (animateTransition)
        {
            if (animCoroutine != null) StopCoroutine(animCoroutine);
            animCoroutine = StartCoroutine(AnimateLineWidth(targetWidth));
        }
        else
        {
            SetLineWidth(targetWidth);
        }
    }

    public void SetWide(bool wide)
    {
        isWide = wide;
        float targetWidth = isWide ? wideLineWidth : normalLineWidth;

        if (animateTransition)
        {
            if (animCoroutine != null) StopCoroutine(animCoroutine);
            animCoroutine = StartCoroutine(AnimateLineWidth(targetWidth));
        }
        else
        {
            SetLineWidth(targetWidth);
        }
    }

    private void SetLineWidth(float width)
    {
        if (lines == null || !IsFinite(width))
            return;

        foreach (var line in lines)
        {
            if (line == null) continue;
            line.startWidth = width;
            line.endWidth   = width;
        }
    }

    private IEnumerator AnimateLineWidth(float targetWidth)
    {
        if (lines == null || lines.Length == 0)
        {
            animCoroutine = null;
            yield break;
        }

        if (!TryGetCurrentWidth(out float currentWidth))
        {
            animCoroutine = null;
            yield break;
        }

        if (!IsFinite(targetWidth))
        {
            Debug.LogWarning("[LineWidthController] Ignored a non-finite target line width.", this);
            animCoroutine = null;
            yield break;
        }

        if (!IsFinite(currentWidth))
        {
            Debug.LogWarning("[LineWidthController] Replaced a non-finite current line width with the requested target.", this);
            SetLineWidth(targetWidth);
            animCoroutine = null;
            yield break;
        }

        if (!IsFinite(widthLerpSpeed) || widthLerpSpeed <= 0f)
        {
            Debug.LogWarning("[LineWidthController] Width interpolation speed must be finite and greater than zero. Applied the target width immediately.", this);
            SetLineWidth(targetWidth);
            animCoroutine = null;
            yield break;
        }

        while (!Mathf.Approximately(currentWidth, targetWidth))
        {
            if (!HasAnyValidLine())
            {
                animCoroutine = null;
                yield break;
            }

            currentWidth = Mathf.Lerp(currentWidth, targetWidth, Time.deltaTime * widthLerpSpeed);
            SetLineWidth(currentWidth);
            yield return null;
        }

        SetLineWidth(targetWidth);
        animCoroutine = null;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private bool TryGetCurrentWidth(out float width)
    {
        width = 0f;

        if (lines == null)
            return false;

        foreach (var line in lines)
        {
            if (line == null) continue;
            width = line.startWidth;
            return true;
        }

        return false;
    }

    private bool HasAnyValidLine()
    {
        if (lines == null)
            return false;

        foreach (var line in lines)
        {
            if (line != null)
                return true;
        }

        return false;
    }
}
