using UnityEngine;
using MixedReality.Toolkit.UX; 

public class TransformSliderControl : MonoBehaviour
{
    public Transform targetObject;

    public float minScale = 0.5f;

    public float maxScale = 2.0f;

    [Header("Sensitivity")]
    [Range(0.2f, 4f)] public float upSensitivity = 1.0f;

    private void Awake()
    {
        if (targetObject == null)
            Debug.LogError("[TransformSliderControl] Target transform is missing; scale updates cannot be applied.", this);

        if (!IsFinite(minScale) || !IsFinite(maxScale) || !IsFinite(upSensitivity))
            Debug.LogWarning("[TransformSliderControl] Scale configuration contains a non-finite value.", this);

        if (minScale > maxScale)
            Debug.LogWarning("[TransformSliderControl] minScale is greater than maxScale; slider output will be reversed.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public void UpdateScaleMRTK(SliderEventData eventData)
    {
        if (targetObject == null) return;

        float newScale = eventData.NewValue;

        targetObject.localScale = new Vector3(newScale, newScale, newScale);
    }

    public void UpdateScaleFloat(float sliderValue)
    {
        if (targetObject == null) return;

        float safeValue = Mathf.Clamp01(sliderValue);
        float shapedValue = Mathf.Pow(safeValue, upSensitivity);
        float newScale = Mathf.Lerp(minScale, maxScale, shapedValue);
        targetObject.localScale = new Vector3(newScale, newScale, newScale);
    }
}
