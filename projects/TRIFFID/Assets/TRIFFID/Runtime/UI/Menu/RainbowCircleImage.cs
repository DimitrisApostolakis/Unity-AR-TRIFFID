using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

public class RainbowCircleImage : MonoBehaviour
{
    [System.Serializable]
    public class ColorPickedEvent : UnityEvent<Color> { }

    [Header("Target")]
    [SerializeField] private Image circleImage;

    [Header("Rainbow")]
    [Range(0f, 2f)] [SerializeField] private float hueSpeed = 0.35f;
    [Range(0f, 1f)] [SerializeField] private float saturation = 1f;
    [Range(0f, 1f)] [SerializeField] private float brightness = 1f;

    [Header("Advanced HSV")]
    [SerializeField] private bool animateSaturation = false;
    [SerializeField] private bool animateBrightness = false;
    [Range(0f, 1f)] [SerializeField] private float saturationMin = 0.55f;
    [Range(0f, 1f)] [SerializeField] private float saturationMax = 1f;
    [Range(0f, 1f)] [SerializeField] private float brightnessMin = 0.55f;
    [Range(0f, 1f)] [SerializeField] private float brightnessMax = 1f;
    [Range(0f, 2f)] [SerializeField] private float saturationSpeed = 0.35f;
    [Range(0f, 2f)] [SerializeField] private float brightnessSpeed = 0.35f;

    [Header("Press Feedback")]
    [SerializeField] private float flashDuration = 0.35f;
    [SerializeField] private bool pulseOnPress = true;
    [Range(1f, 2f)] [SerializeField] private float pulseScale = 1.3f;

    [Header("Events")]
    [SerializeField] private ColorPickedEvent onColorPicked;

    public Color PickedColor { get; private set; }
    public float HueSpeed { get => hueSpeed; set => hueSpeed = value; }
    public float Saturation { get => saturation; set => saturation = value; }
    public float Brightness { get => brightness; set => brightness = value; }
    public float FlashDuration { get => flashDuration; set => flashDuration = value; }
    public bool PulseOnPress { get => pulseOnPress; set => pulseOnPress = value; }
    public float PulseScale { get => pulseScale; set => pulseScale = value; }
    public ColorPickedEvent OnColorPicked => onColorPicked;

    private float _hue;
    private bool  _busy;
    private Coroutine _pressRoutine;
    private Vector3 _baseScale;
    private float _satPhase;
    private float _brightPhase;

    private void Awake()
    {
        if (circleImage == null)
            circleImage = GetComponent<Image>();

        ValidateConfiguration();
        _baseScale = transform.localScale;
    }

    private void ValidateConfiguration()
    {
        if (circleImage == null)
            Debug.LogError("[RainbowCircleImage] Image target is missing; rainbow color animation cannot run.", this);

        if (!IsFinite(hueSpeed) || !IsFinite(saturationSpeed) || !IsFinite(brightnessSpeed) ||
            hueSpeed < 0f || saturationSpeed < 0f || brightnessSpeed < 0f)
        {
            Debug.LogWarning("[RainbowCircleImage] Rainbow animation speeds must be finite and non-negative.", this);
        }

        if (!IsFinite(saturation) || !IsFinite(brightness) ||
            !IsFinite(saturationMin) || !IsFinite(saturationMax) ||
            !IsFinite(brightnessMin) || !IsFinite(brightnessMax))
        {
            Debug.LogWarning("[RainbowCircleImage] HSV configuration contains a non-finite value.", this);
        }

        if (!IsFinite(flashDuration) || flashDuration < 0f)
            Debug.LogWarning("[RainbowCircleImage] flashDuration must be finite and non-negative.", this);

        if (!IsFinite(pulseScale))
            Debug.LogWarning("[RainbowCircleImage] pulseScale must be finite.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void Update()
    {
        if (circleImage == null)
            return;

        if (!_busy) Tick();
    }

    private void OnDisable()
    {
        StopPressAnimationAndRestore();
    }

    private void OnDestroy()
    {
        StopPressAnimationAndRestore();
    }

    private void Tick()
    {
        _hue = (_hue + hueSpeed * Time.deltaTime) % 1f;
        SetColor(CurrentColor());
    }

    private Color CurrentColor() =>
        Color.HSVToRGB(_hue, GetCurrentSaturation(), GetCurrentBrightness());

    private float GetCurrentSaturation()
    {
        float min = Mathf.Min(saturationMin, saturationMax);
        float max = Mathf.Max(saturationMin, saturationMax);

        if (!animateSaturation)
            return Mathf.Clamp01(saturation);

        _satPhase = (_satPhase + saturationSpeed * Time.deltaTime) % 1f;
        return Mathf.Lerp(min, max, Mathf.PingPong(_satPhase * 2f, 1f));
    }

    private float GetCurrentBrightness()
    {
        float min = Mathf.Min(brightnessMin, brightnessMax);
        float max = Mathf.Max(brightnessMin, brightnessMax);

        if (!animateBrightness)
            return Mathf.Clamp01(brightness);

        _brightPhase = (_brightPhase + brightnessSpeed * Time.deltaTime) % 1f;
        return Mathf.Lerp(min, max, Mathf.PingPong(_brightPhase * 2f, 1f));
    }

    private void SetColor(Color c)
    {
        if (circleImage) circleImage.color = c;
    }

    public void OnButtonPressed()
    {
        PickedColor = CurrentColor();
        Debug.Log($"[RainbowCircle] Picked → #{ColorUtility.ToHtmlStringRGB(PickedColor)}");

        onColorPicked?.Invoke(PickedColor);

        if (_pressRoutine != null)
            StopCoroutine(_pressRoutine);

        _pressRoutine = StartCoroutine(PressAnim(PickedColor));
    }

    private IEnumerator PressAnim(Color picked)
    {
        _busy = true;
        Vector3 origScale = _baseScale;
        float   elapsed   = 0f;

        while (elapsed < flashDuration)
        {
            float p = flashDuration > 0f ? elapsed / flashDuration : 1f;

            SetColor(Color.Lerp(Color.white, picked, p));

            if (pulseOnPress)
            {
                float s = p < 0.5f
                    ? Mathf.Lerp(1f, pulseScale, p * 2f)
                    : Mathf.Lerp(pulseScale, 1f, (p - 0.5f) * 2f);
                transform.localScale = origScale * s;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        transform.localScale = origScale;

        Color.RGBToHSV(picked, out _hue, out _, out _);

        _busy = false;
        _pressRoutine = null;
    }

    private void StopPressAnimationAndRestore()
    {
        if (_pressRoutine != null)
        {
            StopCoroutine(_pressRoutine);
            _pressRoutine = null;
        }

        transform.localScale = _baseScale;
        _busy = false;
    }
}
