using UnityEngine;
using System.Collections.Generic;

public class MenuStateRestorer : MonoBehaviour
{
    private enum BridgeSignal
    {
        None,
        LeftPrimaryPressed,
        LeftGripPressed,
        RightPrimaryPressed,
        RightGripPressed,
        RightStickClicked
    }

    [System.Serializable]
    private class PanelRoute
    {
        public GameObject panel;
        public GameObject parent;
    }

    [Header("Main Canvas")]
    [SerializeField] private GameObject menuContentCanvas;
    [SerializeField] private GameObject closeButton;

    [Header("Panel Routes")]
    [SerializeField] private PanelRoute[] panelRoutes;

    [Header("Flow Route Indices")]
    [SerializeField] private int drawMenuRouteIndex = 0;
    [SerializeField] private int colorLinesRouteIndex = 1;
    [SerializeField] private int polygonClassesRouteIndex = 2;

    [Header("Input Bridge")]
    [SerializeField] private XRControllerInputBridge inputBridge;
    [SerializeField] private bool enableBridgeAutoOpenClose = false;
    [SerializeField] private BridgeSignal handDetectedSignal = BridgeSignal.None;
    [SerializeField] private BridgeSignal handLostSignal = BridgeSignal.None;

    [Header("Mode Guard")]
    [SerializeField] private MenuButtonController menuButtonController;

    private readonly Stack<GameObject> _panelStack = new Stack<GameObject>();
    private readonly Dictionary<GameObject, GameObject> _parentByPanel = new Dictionary<GameObject, GameObject>();
    private bool _menuManuallyHidden;

    private void Awake()
    {
        ValidateConfiguration();
        BuildPanelRouteMap();
        ResetToMainMenu();
    }

    private void ValidateConfiguration()
    {
        if (menuContentCanvas == null)
            Debug.LogError("[MenuStateRestorer] Main menu canvas is missing; panel navigation cannot run.", this);

        if (closeButton == null)
            Debug.LogWarning("[MenuStateRestorer] Close button is missing; close-button visibility cannot be synchronized.", this);

        if (panelRoutes == null || panelRoutes.Length == 0)
        {
            Debug.LogError("[MenuStateRestorer] Panel route table is empty; routed menu navigation cannot run.", this);
        }
        else
        {
            ValidateRouteIndex(nameof(drawMenuRouteIndex), drawMenuRouteIndex);
            ValidateRouteIndex(nameof(colorLinesRouteIndex), colorLinesRouteIndex);
            ValidateRouteIndex(nameof(polygonClassesRouteIndex), polygonClassesRouteIndex);

            for (int i = 0; i < panelRoutes.Length; i++)
            {
                PanelRoute route = panelRoutes[i];
                if (route == null || route.panel == null)
                    Debug.LogWarning($"[MenuStateRestorer] Panel route {i} has no target panel.", this);
            }
        }

        if (enableBridgeAutoOpenClose)
        {
            if (inputBridge == null)
                Debug.LogError("[MenuStateRestorer] Input bridge is missing while bridge auto-open/close is enabled.", this);

            if (handDetectedSignal == BridgeSignal.None)
                Debug.LogWarning("[MenuStateRestorer] Hand-detected signal is None; automatic menu opening will not be triggered.", this);

            if (handLostSignal == BridgeSignal.None)
                Debug.LogWarning("[MenuStateRestorer] Hand-lost signal is None; automatic menu closing will not be triggered.", this);
        }

        if (menuButtonController == null)
            Debug.LogWarning("[MenuStateRestorer] MenuButtonController is missing; Draw-mode navigation guard is unavailable.", this);
    }

    private void ValidateRouteIndex(string fieldName, int index)
    {
        if (index < 0 || index >= panelRoutes.Length)
            Debug.LogError($"[MenuStateRestorer] {fieldName} {index} is outside panelRoutes length {panelRoutes.Length}.", this);
    }

    private void OnEnable()
    {
        SubscribeBridgeSignals();
    }

    private void OnDisable()
    {
        UnsubscribeBridgeSignals();
    }

    private void LateUpdate()
    {
        if (IsDrawModeActive())
            return;

        if (_menuManuallyHidden)
            return;

        if (_panelStack.Count == 0)
        {
            ResetToMainMenu();
            return;
        }

        GameObject current = _panelStack.Peek();
        if (current == null || !current.activeSelf)
            ResetToMainMenu();
    }

    public void OnHandDetected()
    {
        if (IsDrawModeActive())
            return;

        if (menuContentCanvas == null)
            return;

        if (IsAnySidePanelOpen())
            return;

        _menuManuallyHidden = false;
        menuContentCanvas.SetActive(true);
        if (_panelStack.Count == 0)
            _panelStack.Push(menuContentCanvas);

        SyncCloseButton();
    }

    public void OnHandLost()
    {
        if (IsDrawModeActive())
            return;

        if (menuContentCanvas != null)
            menuContentCanvas.SetActive(false);

        foreach (var panel in _parentByPanel.Keys)
        {
            if (panel != null)
                panel.SetActive(false);
        }

        _panelStack.Clear();
        SyncCloseButton();
    }

    public void OpenPanel(GameObject targetPanel)
    {
        if (targetPanel == null || menuContentCanvas == null)
            return;

        _menuManuallyHidden = false;

        GameObject parentPanel = ResolveParentPanel(targetPanel);
        GameObject activeSidePanel = GetActiveSidePanelExcept(targetPanel);
        if (activeSidePanel != null)
            parentPanel = activeSidePanel;

        if (_panelStack.Count > 0)
        {
            GameObject currentTop = _panelStack.Peek();
            if (currentTop != null && currentTop.activeSelf && currentTop != targetPanel)
                parentPanel = currentTop;
        }

        if (parentPanel == null)
            parentPanel = menuContentCanvas;

        if (_panelStack.Count == 0)
            _panelStack.Push(menuContentCanvas);

        if (_panelStack.Peek() != parentPanel)
            RebuildStackToParent(parentPanel);

        GameObject current = _panelStack.Peek();
        SnapToPanel(current, targetPanel);

        current.SetActive(false);
        targetPanel.SetActive(true);

        if (_panelStack.Count == 0 || _panelStack.Peek() != targetPanel)
            _panelStack.Push(targetPanel);

        SyncCloseButton();
    }

    public void ClosePanel(GameObject currentPanel)
    {
        if (currentPanel == null || _panelStack.Count == 0)
            return;

        if (_panelStack.Peek() != currentPanel)
        {
            ResetToMainMenu();
            return;
        }

        if (_panelStack.Count == 1)
        {
            SyncCloseButton();
            return;
        }

        GameObject current = _panelStack.Pop();
        GameObject parent = _panelStack.Peek();

        if (current != null && parent != null)
            SnapToPanel(current, parent);

        if (current != null)
            current.SetActive(false);
        if (parent != null)
            parent.SetActive(true);

        SyncCloseButton();
    }

    public void OpenPanel(int index)
    {
        OpenPanel(GetPanelByIndex(index));
    }

    public void ClosePanel(int index)
    {
        ClosePanel(GetPanelByIndex(index));
    }

    public void OpenLinesFlow()
    {
        GameObject colorLinesPanel = GetPanelByIndex(colorLinesRouteIndex);
        OpenPanel(colorLinesPanel);
    }

    public void OpenPolygonFlow()
    {
        GameObject polygonClassesPanel = GetPanelByIndex(polygonClassesRouteIndex);
        OpenPanel(polygonClassesPanel);
    }

    public void OpenColorLinesFromPolygonFlow()
    {
        GameObject polygonClassesPanel = GetPanelByIndex(polygonClassesRouteIndex);
        GameObject colorLinesPanel = GetPanelByIndex(colorLinesRouteIndex);

        if (polygonClassesPanel != null && (_panelStack.Count == 0 || _panelStack.Peek() != polygonClassesPanel))
            OpenPanel(polygonClassesPanel);

        OpenPanel(colorLinesPanel);
    }

    public void BackFromColorLines()
    {
        ClosePanel(GetPanelByIndex(colorLinesRouteIndex));
    }

    public void BackFromPolygonClasses()
    {
        ClosePanel(GetPanelByIndex(polygonClassesRouteIndex));
    }

    public void OpenDrawMenuRoot()
    {
        GameObject drawMenuPanel = GetPanelByIndex(drawMenuRouteIndex);
        OpenPanel(drawMenuPanel);
    }

    public void ResetToMainMenu()
    {
        _menuManuallyHidden = false;
        _panelStack.Clear();

        if (menuContentCanvas != null)
        {
            menuContentCanvas.SetActive(true);
            _panelStack.Push(menuContentCanvas);
        }

        foreach (var panel in _parentByPanel.Keys)
        {
            if (panel != null)
                panel.SetActive(false);
        }

        SyncCloseButton();
    }

    public void CloseMenuFromButton()
    {
        if (menuContentCanvas != null)
            menuContentCanvas.SetActive(false);

        foreach (var panel in _parentByPanel.Keys)
        {
            if (panel != null)
                panel.SetActive(false);
        }

        _panelStack.Clear();
        _menuManuallyHidden = true;
        SyncCloseButton();
    }

    private void BuildPanelRouteMap()
    {
        _parentByPanel.Clear();

        if (panelRoutes == null)
            return;

        foreach (var route in panelRoutes)
        {
            if (route == null || route.panel == null)
                continue;

            GameObject parent = route.parent != null ? route.parent : menuContentCanvas;
            _parentByPanel[route.panel] = parent;
        }
    }

    private GameObject ResolveParentPanel(GameObject panel)
    {
        if (panel == null)
            return null;

        if (_parentByPanel.TryGetValue(panel, out GameObject parent))
            return parent;

        return menuContentCanvas;
    }

    private void RebuildStackToParent(GameObject parentPanel)
    {
        if (menuContentCanvas == null)
            return;

        _panelStack.Clear();
        _panelStack.Push(menuContentCanvas);

        if (parentPanel != null && parentPanel != menuContentCanvas)
            _panelStack.Push(parentPanel);
    }

    private GameObject GetPanelByIndex(int index)
    {
        if (panelRoutes == null || index < 0 || index >= panelRoutes.Length)
            return null;

        PanelRoute route = panelRoutes[index];
        return route != null ? route.panel : null;
    }

    private void SyncCloseButton()
    {
        if (closeButton == null)
            return;

        bool mainVisible = menuContentCanvas != null && menuContentCanvas.activeSelf;
        bool onMainPanel = _panelStack.Count > 0 && _panelStack.Peek() == menuContentCanvas;
        closeButton.SetActive(mainVisible && onMainPanel);
    }

    private static void SnapToPanel(GameObject from, GameObject to)
    {
        if (from == null || to == null)
            return;

        to.transform.position = from.transform.position;
        to.transform.rotation = from.transform.rotation;
    }

    private bool IsAnySidePanelOpen()
    {
        foreach (var panel in _parentByPanel.Keys)
        {
            if (panel != null && panel.activeSelf)
                return true;
        }

        return false;
    }

    private GameObject GetActiveSidePanelExcept(GameObject excludedPanel)
    {
        if (panelRoutes == null)
            return null;

        for (int i = panelRoutes.Length - 1; i >= 0; i--)
        {
            PanelRoute route = panelRoutes[i];
            GameObject panel = route != null ? route.panel : null;
            if (panel == null || panel == excludedPanel)
                continue;

            if (panel.activeSelf)
                return panel;
        }

        return null;
    }

    private void SubscribeBridgeSignals()
    {
        if (!enableBridgeAutoOpenClose || inputBridge == null)
            return;

        BindBridgeSignal(handDetectedSignal, OnHandDetected, true);
        BindBridgeSignal(handLostSignal, OnHandLost, true);
    }

    private void UnsubscribeBridgeSignals()
    {
        if (!enableBridgeAutoOpenClose || inputBridge == null)
            return;

        BindBridgeSignal(handDetectedSignal, OnHandDetected, false);
        BindBridgeSignal(handLostSignal, OnHandLost, false);
    }

    private void BindBridgeSignal(BridgeSignal signal, System.Action callback, bool subscribe)
    {
        if (callback == null || inputBridge == null)
            return;

        switch (signal)
        {
            case BridgeSignal.LeftPrimaryPressed:
                if (subscribe) inputBridge.LeftPrimaryPressed += callback;
                else inputBridge.LeftPrimaryPressed -= callback;
                break;
            case BridgeSignal.LeftGripPressed:
                if (subscribe) inputBridge.LeftGripPressed += callback;
                else inputBridge.LeftGripPressed -= callback;
                break;
            case BridgeSignal.RightPrimaryPressed:
                if (subscribe) inputBridge.RightPrimaryPressed += callback;
                else inputBridge.RightPrimaryPressed -= callback;
                break;
            case BridgeSignal.RightGripPressed:
                if (subscribe) inputBridge.RightGripPressed += callback;
                else inputBridge.RightGripPressed -= callback;
                break;
            case BridgeSignal.RightStickClicked:
                if (subscribe) inputBridge.RightStickClicked += callback;
                else inputBridge.RightStickClicked -= callback;
                break;
        }
    }

    private bool IsDrawModeActive()
    {
        if (menuButtonController == null)
            return false;

        return menuButtonController.IsDrawModeActive();
    }
}
