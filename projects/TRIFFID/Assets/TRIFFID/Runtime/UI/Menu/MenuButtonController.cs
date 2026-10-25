using UnityEngine;
using UnityEngine.UI;
using MixedReality.Toolkit.UX;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.Serialization;
using MRTKSlider = MixedReality.Toolkit.UX.Slider;
using UISlider = UnityEngine.UI.Slider;

public class MenuButtonController : MonoBehaviour
{
    private enum MenuState
    {
        Locked,
        Loading,
        Walk,
        Info,
        Draw
    }

    [Header("Load Flow")]
    [Tooltip("Primary button that unlocks configured controls and invokes the load event once.")]
    [SerializeField] private PressableButton loadButton;

    [Header("Unity Events")]
    [Tooltip("Invoked when the load button is accepted for the first time.")]
    [SerializeField] private UnityEvent onLoadPressed;
    [Tooltip("Invoked when the locked UI state becomes active.")]
    [SerializeField] private UnityEvent onLockUiEnabled;
    [Tooltip("Invoked when the locked UI state is released.")]
    [SerializeField] private UnityEvent onLockUiDisabled;

    [Header("Pre-load Buttons")]
    [Tooltip("Buttons kept non-interactable until the load action completes.")]
    [SerializeField] private PressableButton[] buttonsToDisable;

    [Header("Pre-load Sliders")]
    [Tooltip("MRTK sliders kept non-interactable until the load action completes.")]
    [SerializeField] private MRTKSlider[] mrtkSlidersToDisable;
    [Tooltip("Unity UI sliders kept non-interactable until the load action completes.")]
    [SerializeField] private UISlider[] uiSlidersToDisable;
    [Tooltip("Slider labels or graphics whose disabled visual state follows the pre-load sliders.")]
    [SerializeField] private Graphic[] sliderTextGraphics;

    [Header("Mode Buttons")]
    [Tooltip("Button associated with Walk mode.")]
    [SerializeField] private PressableButton walkButton;
    [Tooltip("Button associated with Info mode.")]
    [SerializeField] private PressableButton infoButton;
    [Tooltip("Button associated with Draw mode.")]
    [SerializeField] private PressableButton drawButton;
    [Tooltip("Button associated with Lock mode.")]
    [SerializeField] private PressableButton lockButton;
    [Tooltip("Button invoked when leaving Walk mode to restore the map and XR rig.")]
    [SerializeField] private PressableButton resetButton;
    [Tooltip("Allows the XR right-stick click to toggle the lock state directly.")]
    [SerializeField] private bool useLockClickedToggle = true;

    [Header("Lock Integration")]
    [Tooltip("Collider whose enabled state follows the lock interaction state.")]
    [SerializeField] private Collider colliderToToggleOnLock;

    [Header("Walk Mode Colliders")]
    [Tooltip("Primary map collider managed while entering or leaving Walk mode.")]
    [SerializeField] private Collider mapCollider;
    [Tooltip("Map-origin bounds collider managed together with the primary map collider.")]
    [SerializeField] private Collider mapOriginBoundsCollider;
    [Tooltip("Keeps map colliders disabled for the entire duration of Walk mode.")]
    [SerializeField] private bool keepMapColliderDisabledWhileWalk = true;

    [Header("Walk Mode Integration")]
    [Tooltip("Optional component that applies the configured Walk-mode map scale.")]
    [SerializeField] private ScaleChanger scaleChanger;
    [Tooltip("Optional component that changes the camera background for Walk mode.")]
    [SerializeField] private CameraBackgroundToggle cameraBackgroundToggle;

    [Header("Disabled Visuals")]
    [Tooltip("Applies reduced alpha and saturation to controls while they are non-interactable.")]
    [SerializeField] private bool useBlurLikeDisabledVisual = true;
    [Tooltip("Alpha multiplier applied to disabled control graphics.")]
    [Range(0.1f, 1f)] [SerializeField] private float disabledAlpha = 0.45f;
    [Tooltip("Color saturation retained by disabled control graphics.")]
    [Range(0f, 1f)] [SerializeField] private float disabledSaturation = 0.1f;

    [Header("Locomotion")]
    [Tooltip("Locomotion component enabled or disabled according to the active menu mode.")]
    [SerializeField] private FreeLocomotion locomotionComponent;

    [Header("Menu Recenter")]
    [Tooltip("Controller responsible for repositioning the hand and secondary menus near the user.")]
    [SerializeField] private XRControllerLogger xrControllerLogger;
    [Tooltip("XR input source used for lock toggling and menu recenter requests.")]
    [SerializeField] private XRControllerInputBridge inputBridge;

    [Header("Draw Mode Integration")]
    [FormerlySerializedAs("menuContentCanvas")]
    [Tooltip("Main menu origin shown when entering Draw mode.")]
    [SerializeField] private GameObject menuOrigin;
    [Tooltip("Root of the drawing-tools menu.")]
    [SerializeField] private GameObject drawMenu;
    [Tooltip("Close control coordinated with the Draw-mode menu flow.")]
    [SerializeField] private GameObject closeButton;
    [Tooltip("Map manipulation controls shown or hidden by mode transitions.")]
    [SerializeField] private GameObject manipulationBar;
    [Tooltip("Optional ghost representation used while configuring or manipulating the map.")]
    [SerializeField] private GameObject mapGhost;
    [Tooltip("Optional annotation spawn menu coordinated with Draw mode.")]
    [SerializeField] private GameObject spawnMenu;
    [Tooltip("Component that receives SyncToMap after relevant map-state transitions.")]
    [SerializeField] private MonoBehaviour mapColliderSync;

    [Header("Info Mode Integration")]
    [Tooltip("JsonSpawner whose marker colliders are enabled or disabled for Info mode.")]
    [SerializeField] private JsonSpawner jsonSpawner;

    private bool isLoaded = false;
    private MenuState currentState = MenuState.Loading;
    private bool walkModeActive;
    private Coroutine enterDrawRoutine;
    private Coroutine walkUntoggleRoutine;
    private Coroutine mapSyncRoutine;
    private bool initialMapColliderCaptured;
    private bool initialMapColliderEnabled;
    private bool initialMapOriginBoundsColliderCaptured;
    private bool initialMapOriginBoundsColliderEnabled;
    private bool hasLockVisualState;
    private bool lastLockVisualEnabled;
    private bool lockMapColliderStateCaptured;
    private bool lockMapOriginBoundsColliderStateCaptured;
    private bool mapColliderEnabledBeforeLock;
    private bool mapOriginBoundsColliderEnabledBeforeLock;

    private readonly Dictionary<Graphic, Color> originalGraphicColors = new();

    private void Start()
    {
        ValidateDependencies();

        if (mapCollider != null)
        {
            initialMapColliderEnabled = mapCollider.enabled;
            initialMapColliderCaptured = true;
        }

        if (mapOriginBoundsCollider != null)
        {
            initialMapOriginBoundsColliderEnabled = mapOriginBoundsCollider.enabled;
            initialMapOriginBoundsColliderCaptured = true;
        }

        SetLocomotionEnabled(false);

        SetButtonsInteractable(false);
        SetSlidersInteractable(false);

        if (loadButton != null)
            loadButton.OnClicked.AddListener(OnLoadPressed);

        TransitionToState(MenuState.Loading);
    }

    private void OnEnable()
    {
        if (inputBridge == null)
            return;

        if (useLockClickedToggle)
            inputBridge.RightStickClicked += OnLockClickedToggle;
        inputBridge.LeftPrimaryPressed += OnMenuRecenterRequested;
    }

    private void OnDisable()
    {
        if (inputBridge == null)
            return;

        if (useLockClickedToggle)
            inputBridge.RightStickClicked -= OnLockClickedToggle;
        inputBridge.LeftPrimaryPressed -= OnMenuRecenterRequested;
    }

    private void OnLoadPressed()
    {
        if (isLoaded)
            return;

        isLoaded = true;
        SetButtonInteractable(loadButton, false);
        TransitionToState(MenuState.Loading);
        onLoadPressed?.Invoke();
    }

    public void OnWalkToggled()
    {
        walkModeActive = true;

        if (scaleChanger != null)
            scaleChanger.ApplyNewScale();

        if (cameraBackgroundToggle != null)
            cameraBackgroundToggle.SetCameraBackground(true);

        if (currentState == MenuState.Info || currentState == MenuState.Draw)
            TransitionToState(currentState);
        else
            TransitionToState(MenuState.Walk);

        RecenterMenusNearUser();
    }

    public void OnWalkUntoggled()
    {
        if (cameraBackgroundToggle != null)
            cameraBackgroundToggle.SetCameraBackground(false);

        if (resetButton != null)
            resetButton.OnClicked?.Invoke();

        if (walkUntoggleRoutine != null)
            StopCoroutine(walkUntoggleRoutine);

        walkUntoggleRoutine = StartCoroutine(HandleWalkUntoggleTransition());
    }

    public void OnInfoToggled()
    {
        if (jsonSpawner != null)
            jsonSpawner.SetAllColliders(true);

        TransitionToState(MenuState.Info);
    }

    public void OnInfoUntoggled()
    {
        if (jsonSpawner != null)
            jsonSpawner.SetAllColliders(false);

        if (currentState == MenuState.Info)
            TransitionToState(walkModeActive ? MenuState.Walk : MenuState.Loading);
    }

    public void OnDrawToggled()
    {
        if (mapCollider != null)
            mapCollider.enabled = false;

        if (mapOriginBoundsCollider != null)
            mapOriginBoundsCollider.enabled = false;

        if (drawMenu != null)
            drawMenu.SetActive(true);

        if (xrControllerLogger != null)
            xrControllerLogger.ToggleAnnotationMode();

        RequestMapSync();

        if (mapGhost != null)
        {
            mapGhost.SetActive(true);

            Collider[] ghostColliders = mapGhost.GetComponentsInChildren<Collider>(true);
            foreach (var col in ghostColliders)
                col.enabled = true;
        }

        if (jsonSpawner != null)
            jsonSpawner.SetAllColliders(true);

        if (closeButton != null)
            closeButton.SetActive(false);

        if (manipulationBar != null)
            manipulationBar.SetActive(false);

        if (enterDrawRoutine != null)
            StopCoroutine(enterDrawRoutine);

        enterDrawRoutine = StartCoroutine(EnterDrawAfterButtonCallback());
    }

    public void OnDrawUntoggled()
    {
        if (currentState == MenuState.Draw)
        {
            if (enterDrawRoutine != null)
            {
                StopCoroutine(enterDrawRoutine);
                enterDrawRoutine = null;
            }

            if (menuOrigin != null && !menuOrigin.activeSelf)
                menuOrigin.SetActive(true);

            if (drawMenu != null)
                drawMenu.SetActive(false);

            if (mapGhost != null)
            {
                mapGhost.SetActive(false);

                Collider[] ghostColliders = mapGhost.GetComponentsInChildren<Collider>(true);
                foreach (var col in ghostColliders)
                    col.enabled = false;
            }

            RequestMapSync();

            if (jsonSpawner != null)
                jsonSpawner.SetAllColliders(false);

            if (xrControllerLogger != null)
                xrControllerLogger.ToggleAnnotationMode();

            if (xrControllerLogger != null)
                xrControllerLogger.ExportAnnotationsToGeoJson();

            if (spawnMenu != null)
                spawnMenu.SetActive(false);

            TransitionToState(walkModeActive ? MenuState.Walk : MenuState.Loading);
        }
    }

    private IEnumerator HandleWalkUntoggleTransition()
    {
        yield return null;

        if (currentState == MenuState.Info || currentState == MenuState.Draw)
        {
            walkUntoggleRoutine = null;
            yield break;
        }

        walkModeActive = false;

        if (currentState == MenuState.Walk)
            TransitionToState(MenuState.Loading);

        walkUntoggleRoutine = null;
    }

    private IEnumerator EnterDrawAfterButtonCallback()
    {
        yield return null;

        if (menuOrigin != null)
            menuOrigin.SetActive(false);

        TransitionToState(MenuState.Draw);
        enterDrawRoutine = null;
    }

    private void RequestMapSync()
    {
        if (mapColliderSync == null)
            return;

        mapColliderSync.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);

        if (mapSyncRoutine != null)
            StopCoroutine(mapSyncRoutine);

        mapSyncRoutine = StartCoroutine(SyncMapNextFrame());
    }

    private IEnumerator SyncMapNextFrame()
    {
        yield return null;

        if (mapColliderSync != null)
            mapColliderSync.SendMessage("SyncToMap", SendMessageOptions.DontRequireReceiver);

        mapSyncRoutine = null;
    }

    public void OnLockToggled()
    {
        if (colliderToToggleOnLock != null)
            colliderToToggleOnLock.enabled = false;

        if (mapCollider != null)
        {
            mapColliderEnabledBeforeLock = mapCollider.enabled;
            lockMapColliderStateCaptured = true;
            mapCollider.enabled = false;
        }

        if (mapOriginBoundsCollider != null)
        {
            mapOriginBoundsColliderEnabledBeforeLock = mapOriginBoundsCollider.enabled;
            lockMapOriginBoundsColliderStateCaptured = true;
            mapOriginBoundsCollider.enabled = false;
        }

        SetLocomotionEnabled(false);

        TransitionToState(MenuState.Locked);
    }

    public void OnLockUntoggled()
    {
        if (colliderToToggleOnLock != null)
            colliderToToggleOnLock.enabled = true;

        if (mapCollider != null && lockMapColliderStateCaptured)
        {
            mapCollider.enabled = mapColliderEnabledBeforeLock;
            lockMapColliderStateCaptured = false;
        }

        if (mapOriginBoundsCollider != null && lockMapOriginBoundsColliderStateCaptured)
        {
            mapOriginBoundsCollider.enabled = mapOriginBoundsColliderEnabledBeforeLock;
            lockMapOriginBoundsColliderStateCaptured = false;
        }

        lockMapColliderStateCaptured = false;

        if (currentState == MenuState.Locked)
            TransitionToState(MenuState.Loading);
    }

    public void OnLockClickedToggle()
    {
        if (!useLockClickedToggle)
            return;

        if (!isLoaded)
            return;

        if (!CanUseLockInCurrentState())
            return;

        if (currentState == MenuState.Locked)
            OnLockUntoggled();
        else
            OnLockToggled();
    }

    private void TransitionToState(MenuState newState)
    {
        currentState = newState;

        if (loadButton != null)
            SetButtonInteractable(loadButton, !isLoaded);

        if (!isLoaded)
        {
            SetButtonInteractable(walkButton, false);
            SetButtonInteractable(infoButton, false);
            SetButtonInteractable(drawButton, false);
            SetButtonInteractable(lockButton, false);
            SetButtonInteractable(resetButton, false);
            UpdateLockVisualState(false);
            SetSlidersInteractable(false);
            SetLocomotionEnabled(false);
            EnforceMapColliderRule();
            return;
        }

        SetButtonsInteractable(true);
        SetSlidersInteractable(true);

        bool walkEnabled = true;
        bool infoEnabled = true;
        bool drawEnabled = true;
        bool lockEnabled = true;
        bool resetEnabled = true;
        bool locomotionEnabled = ShouldEnableLocomotionForState(currentState);

        if (walkModeActive)
        {
            lockEnabled = false;
            resetEnabled = false;
        }

        if (currentState == MenuState.Info)
        {
            drawEnabled = false;
            lockEnabled = false;
        }

        if (currentState == MenuState.Draw)
        {
            infoEnabled = false;
            lockEnabled = false;
        }

        SetButtonInteractable(walkButton, walkEnabled);
        SetButtonInteractable(infoButton, infoEnabled);
        SetButtonInteractable(drawButton, drawEnabled);
        SetButtonInteractable(lockButton, lockEnabled);
        UpdateLockVisualState(currentState == MenuState.Locked);
        SetButtonInteractable(resetButton, resetEnabled);

        SetSlidersInteractable(currentState != MenuState.Walk);
        SetLocomotionEnabled(locomotionEnabled);
        EnforceMapColliderRule();
    }

    private bool CanUseLockInCurrentState()
    {
        if (currentState == MenuState.Locked)
            return true;

        if (walkModeActive)
            return false;

        if (currentState == MenuState.Info || currentState == MenuState.Draw)
            return false;

        return true;
    }

    public bool IsScaleAdjustmentAllowed()
    {
        return currentState == MenuState.Walk || currentState == MenuState.Info;
    }

    public bool IsDrawModeActive()
    {
        return currentState == MenuState.Draw;
    }

    private void LateUpdate()
    {
        EnforceMapColliderRule();
    }

    private void EnforceMapColliderRule()
    {
        if (!keepMapColliderDisabledWhileWalk || (mapCollider == null && mapOriginBoundsCollider == null)) return;

        bool activeToolMode = walkModeActive || currentState == MenuState.Walk || currentState == MenuState.Info || currentState == MenuState.Draw || currentState == MenuState.Locked;
        if (activeToolMode)
        {
            SetMapColliderStates(false);
            return;
        }

        if (currentState == MenuState.Loading)
        {
            if (initialMapColliderCaptured && mapCollider != null && mapCollider.enabled != initialMapColliderEnabled)
                mapCollider.enabled = initialMapColliderEnabled;

            if (initialMapOriginBoundsColliderCaptured && mapOriginBoundsCollider != null && mapOriginBoundsCollider.enabled != initialMapOriginBoundsColliderEnabled)
                mapOriginBoundsCollider.enabled = initialMapOriginBoundsColliderEnabled;
        }
    }

    private void SetMapColliderStates(bool enabled)
    {
        if (mapCollider != null)
            mapCollider.enabled = enabled;

        if (mapOriginBoundsCollider != null)
            mapOriginBoundsCollider.enabled = enabled;
    }

    private bool ShouldEnableLocomotionForState(MenuState state)
    {
        return state != MenuState.Locked;
    }

    private void UpdateLockVisualState(bool enabled)
    {
        if (!hasLockVisualState || lastLockVisualEnabled != enabled)
        {
            hasLockVisualState = true;
            lastLockVisualEnabled = enabled;

            if (enabled)
                onLockUiEnabled?.Invoke();
            else
                onLockUiDisabled?.Invoke();
        }
    }

    private void SetButtonsInteractable(bool state)
    {
        foreach (var btn in buttonsToDisable)
        {
            if (btn != null)
                SetButtonInteractable(btn, state);
        }
    }

    private void SetSlidersInteractable(bool state)
    {
        foreach (var slider in mrtkSlidersToDisable)
        {
            if (slider == null) continue;

            slider.enabled = state;

            Collider[] colliders = slider.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
                col.enabled = state;

            ApplyButtonVisualState(slider.gameObject, state);
        }

        foreach (var slider in uiSlidersToDisable)
        {
            if (slider != null)
            {
                slider.interactable = state;
                ApplyButtonVisualState(slider.gameObject, state);
            }
        }

        foreach (var textGraphic in sliderTextGraphics)
        {
            if (textGraphic != null)
                ApplyButtonVisualState(textGraphic.gameObject, state);
        }
    }

    private void SetButtonInteractable(PressableButton button, bool state)
    {
        if (button == null) return;

        button.enabled = state;

        Collider[] colliders = button.GetComponentsInChildren<Collider>(true);
        foreach (var collider in colliders)
            collider.enabled = state;

        ApplyButtonVisualState(button.gameObject, state);
    }

    private void ApplyButtonVisualState(GameObject buttonRoot, bool interactable)
    {
        if (!useBlurLikeDisabledVisual || buttonRoot == null) return;

        EnsureVisualColorCache(buttonRoot);

        Graphic[] graphics = buttonRoot.GetComponentsInChildren<Graphic>(true);
        foreach (var graphic in graphics)
        {
            if (graphic == null) continue;

            Color baseColor = originalGraphicColors[graphic];

            if (interactable)
            {
                graphic.color = baseColor;
            }
            else
            {
                float gray = baseColor.grayscale;
                Color grayscaleColor = new Color(gray, gray, gray, baseColor.a);
                Color blurredLike = Color.Lerp(grayscaleColor, baseColor, disabledSaturation);
                blurredLike.a = baseColor.a * disabledAlpha;
                graphic.color = blurredLike;
            }
        }
    }

    private void EnsureVisualColorCache(GameObject root)
    {
        if (root == null)
            return;

        Graphic[] graphics = root.GetComponentsInChildren<Graphic>(true);
        foreach (var graphic in graphics)
        {
            if (graphic == null || originalGraphicColors.ContainsKey(graphic))
                continue;

            originalGraphicColors[graphic] = graphic.color;
        }
    }

    private void OnDestroy()
    {
        if (loadButton != null)
            loadButton.OnClicked.RemoveListener(OnLoadPressed);
    }

    private void SetLocomotionEnabled(bool state)
    {
        if (locomotionComponent == null)
            return;

        locomotionComponent.enabled = state;
    }

    private void ValidateDependencies()
    {
        if (loadButton == null)
            Debug.LogError("[MenuButtonController] Load button is missing; load flow cannot be started.", this);

        if (walkButton == null)
            Debug.LogWarning("[MenuButtonController] Walk mode button is not assigned.", this);

        if (infoButton == null)
            Debug.LogWarning("[MenuButtonController] Info mode button is not assigned.", this);

        if (drawButton == null)
            Debug.LogWarning("[MenuButtonController] Draw mode button is not assigned.", this);

        if (lockButton == null)
            Debug.LogWarning("[MenuButtonController] Lock mode button is not assigned.", this);

        if (resetButton == null)
            Debug.LogWarning("[MenuButtonController] Reset button is not assigned.", this);

        if (locomotionComponent == null)
            Debug.LogWarning("[MenuButtonController] FreeLocomotion is not assigned; Walk-mode locomotion control is unavailable.", this);

        if (xrControllerLogger == null)
            Debug.LogWarning("[MenuButtonController] XRControllerLogger is not assigned; menu recenter and Draw-mode integration are unavailable.", this);

        if (inputBridge == null)
            Debug.LogWarning("[MenuButtonController] XRControllerInputBridge is not assigned; controller shortcuts are unavailable.", this);

        if (jsonSpawner == null)
            Debug.LogWarning("[MenuButtonController] JsonSpawner is not assigned; marker collider mode changes are unavailable.", this);

        if (colliderToToggleOnLock == null)
            Debug.LogWarning("[MenuButtonController] Lock collider is not assigned; lock-state collider control is unavailable.", this);

        if (mapCollider == null)
            Debug.LogWarning("[MenuButtonController] Map collider is not assigned; Walk-mode map collider control is unavailable.", this);

        if (drawMenu == null)
            Debug.LogWarning("[MenuButtonController] Draw menu is not assigned.", this);

        if (menuOrigin == null)
            Debug.LogWarning("[MenuButtonController] Menu origin is not assigned.", this);

        if (closeButton == null)
            Debug.LogWarning("[MenuButtonController] Close button is not assigned.", this);

        if (manipulationBar == null)
            Debug.LogWarning("[MenuButtonController] Manipulation bar is not assigned.", this);

        if (mapGhost == null)
            Debug.LogWarning("[MenuButtonController] Map ghost is not assigned; optional Draw-mode ghost visuals are unavailable.", this);

        if (spawnMenu == null)
            Debug.LogWarning("[MenuButtonController] Spawn menu is not assigned; optional spawn-menu coordination is unavailable.", this);

        if (mapColliderSync == null)
        {
            Debug.LogWarning("[MenuButtonController] Map collider sync component is not assigned.", this);
        }
        else if (mapColliderSync.GetType().GetMethod("SyncToMap", System.Type.EmptyTypes) == null)
        {
            Debug.LogWarning("[MenuButtonController] Map collider sync component does not expose SyncToMap.", this);
        }

        if (mapOriginBoundsCollider == null)
            Debug.LogWarning("[MenuButtonController] Map-origin bounds collider is not assigned.", this);

        if (scaleChanger == null)
            Debug.LogWarning("[MenuButtonController] ScaleChanger is not assigned; optional Walk-mode scale changes are unavailable.", this);

        if (cameraBackgroundToggle == null)
            Debug.LogWarning("[MenuButtonController] CameraBackgroundToggle is not assigned; optional Walk-mode background changes are unavailable.", this);
    }

    private void OnMenuRecenterRequested()
    {
        RecenterMenusNearUser();
    }

    private void RecenterMenusNearUser()
    {
        if (xrControllerLogger != null)
            xrControllerLogger.RecenterMenuNearUser();
    }
}
