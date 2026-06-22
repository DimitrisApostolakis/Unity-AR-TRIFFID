using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
#if UNITY_EDITOR || UNITY_STANDALONE
using UnityEngine.InputSystem;
#endif

public interface IAnnotationPersistenceService
{
    string SavePoint(GameObject pointObject, string pointClass);
    string SaveLine(IReadOnlyList<Vector3> worldPositions, string lineClass, Color color);
    string SavePolygon(IReadOnlyList<Vector3> worldPositions, string polygonClass, string polygonCategory, Color color);
    bool DeleteFeatureByNode(Transform node, bool requireDrawMode = false);
    void RegisterRuntimeLineNodes(string featureId, List<Transform> nodes);
    void RegisterRuntimePolygonNodes(string featureId, List<Transform> nodes);
}

public class XRControllerLogger : MonoBehaviour
{
    private const int FiltersSecondaryMenuIndex = 0;
    private const int InfoPanelSecondaryMenuIndex = 1;

    public enum DrawColor { Red, Yellow, Blue, Custom }
    public enum InteractionState { Navigation, DrawingSinglePoint, DrawingLine, DrawingPolygon }
    public enum PolygonCategory { Safe, Unsafe }

    [Serializable]
    private class SecondaryMenuEntry
    {
        public GameObject menu;
        public Behaviour radialView;
    }

    private sealed class AddLineNodeCommand
    {
        private readonly XRControllerLogger _owner;
        private readonly Vector3 _position;
        private LineSession _line;
        private Transform _node;

        public AddLineNodeCommand(XRControllerLogger owner, Vector3 position)
        {
            _owner = owner;
            _position = position;
        }

        public void Execute()
        {
            if (!_owner.IsLineSessionAlive(_line))
                _line = _owner.GetOrCreateCurrentLine();

            if (!_owner.IsLineSessionAlive(_line))
                return;

            _node = _owner.CreateLineNode(_line, _position);
        }

        public void Undo()
        {
            if (_line == null || _line.IsSaved || _node == null)
                return;

            _owner.RemoveLineNode(_line, _node);
            _node = null;
        }

        public bool TargetsLine(LineSession line) => object.ReferenceEquals(_line, line);
    }

    private sealed class LineSession
    {
        public LineRenderer Renderer;
        public RuntimeLine RuntimeLine;
        public readonly List<Transform> Nodes = new List<Transform>();
        public bool IsSaved;
        public bool IsPolygon;
        public Color StrokeColor;
    }

    [Header("Core Dependencies")]
    [Tooltip("XR input source used by the existing Quest controller bindings.")]
    [SerializeField] private XRControllerInputBridge inputBridge;
    [Tooltip("Optional manipulation behaviour enabled during navigation and disabled while drawing.")]
    [SerializeField] private Behaviour objectManipulator;
    [Tooltip("Adapter that routes annotation save and delete operations through JsonSpawner.")]
    [SerializeField] private JsonSpawnerPersistenceAdapter geoJsonPersistenceProvider;

    [Header("Hand Menu")]
    [Tooltip("Root object positioned and shown when the hand menu is requested.")]
    [SerializeField] private GameObject rootHandMenu;
    [Tooltip("Main menu canvas displayed when no secondary menu is open.")]
    [SerializeField] private GameObject menuContentCanvas;
    [Tooltip("Close control displayed with the main hand menu.")]
    [SerializeField] private GameObject closeButton;
    [Tooltip("Side panels that are mutually coordinated with the main hand menu.")]
    [SerializeField] private GameObject[] sidePanels;
    [Tooltip("Optional head reference used for menu placement. Camera.main is used when this is not assigned.")]
    [SerializeField] private Transform headTransform;
    [Tooltip("Forward distance from the head reference used when recentering the hand menu.")]
    [SerializeField] private float spawnDistanceFromHead = 0.6f;
    [Tooltip("Vertical offset added when recentering the hand menu.")]
    [SerializeField] private float spawnHeightOffset = 0.15f;

    [Header("Secondary Menus")]
    [Tooltip("Reference point used to position secondary menu slots.")]
    [SerializeField] private Transform secondaryMenuReferencePoint;
    [Tooltip("Secondary menu objects and their optional radial-view behaviours.")]
    [SerializeField] private SecondaryMenuEntry[] secondaryMenus = new SecondaryMenuEntry[3];
    [Tooltip("Keeps secondary menus upright instead of inheriting head pitch and roll.")]
    [SerializeField] private bool keepSecondaryMenusVertical = true;
    [Tooltip("Flips the facing direction used when orienting secondary menus toward the user.")]
    [SerializeField] private bool flipSecondaryMenusFacing = true;
    [Tooltip("Additional Euler rotation applied to secondary menus after they face the user.")]
    [SerializeField] private Vector3 secondaryMenusRotationOffset = Vector3.zero;
    [Tooltip("Local slot offset used for the right-side secondary menu position.")]
    [SerializeField] private Vector3 slotOffsetRight = new Vector3(0.35f, 0f, 0f);
    [Tooltip("Local slot offset used for the left-side secondary menu position.")]
    [SerializeField] private Vector3 slotOffsetLeft = new Vector3(-0.35f, 0f, 0f);
    [Tooltip("Local slot offset used for the upper secondary menu position.")]
    [SerializeField] private Vector3 slotOffsetTop = new Vector3(0f, 0.28f, 0f);

    [Header("Menu Orientation")]
    [Tooltip("Keeps the main hand menu upright while it faces the user.")]
    [SerializeField] private bool keepMenuVertical = true;
    [Tooltip("Allows the main menu to track vertical eye direction when facing the user.")]
    [SerializeField] private bool faceEyesWhenLookingUpDown = true;
    [Tooltip("Additional Euler rotation applied after the main menu faces the user.")]
    [SerializeField] private Vector3 additionalRotationOffset = Vector3.zero;

    [Header("Raycast / Placement")]
    [Tooltip("Map bounds collider used for navigation state and as an annotation-parent fallback.")]
    [SerializeField] private BoxCollider mapBoxCollider;
    [Tooltip("Physics layers accepted by annotation placement raycasts.")]
    [SerializeField] private LayerMask mapLayer;

    [Header("Annotation Prefabs and Persistence")]
    [Tooltip("Quest controller ray used by the existing XR annotation placement path.")]
    [SerializeField] private XRRayInteractor rightRayInteractor;
    [Tooltip("Offset applied along the hit normal to keep annotations above the map surface.")]
    [SerializeField] private float baseVerticalOffset = 0.01f;
    [Tooltip("Selectable point prefabs used by single-point drawing mode.")]
    [SerializeField] private GameObject[] availablePointPrefabs;
    [Tooltip("Prefab instantiated for unfinished line and polygon vertices.")]
    [SerializeField] private GameObject lineNodePrefab;
    [Tooltip("Optional parent for newly drawn annotations. The map collider transform is used as a fallback.")]
    [SerializeField] private Transform annotationParent;
    [Tooltip("JsonSpawner used for annotation metadata, map coordinates, and information-panel registration.")]
    [SerializeField] private JsonSpawner jsonSpawner;

    [Header("Debug / Editor Testing")]
    [Tooltip("Enables the Editor/standalone keyboard and mouse drawing fallback. Quest input remains active and unchanged.")]
    [SerializeField] private bool enableKeyboardDebugInput;

    [Header("Line Visuals")]
    [Tooltip("Optional source material copied for newly drawn lines and polygons.")]
    [SerializeField] private Material lineMaterial;
    [Min(0f)]
    [Tooltip("Width assigned to newly drawn line and polygon renderers.")]
    [SerializeField] private float lineWidth = 0.005f;

    [Header("Drawing Colors")]
    [Tooltip("Color used by the Red drawing selection.")]
    [SerializeField] private Color colorRed = Color.red;
    [Tooltip("Color used by the Yellow drawing selection.")]
    [SerializeField] private Color colorYellow = Color.yellow;
    [Tooltip("Color used by the Blue drawing selection.")]
    [SerializeField] private Color colorBlue = Color.blue;

    private InteractionState currentState = InteractionState.Navigation;
    private DrawColor currentColor = DrawColor.Red;
    private PolygonCategory currentPolygonCategory = PolygonCategory.Unsafe;
    private int selectedPrefabIndex = 0;

    private Color _customColor = Color.white;
    private LineSession _currentLine;

    private readonly Stack<AddLineNodeCommand> _undoStack = new Stack<AddLineNodeCommand>();
    private readonly List<LineSession> _lineSessions = new List<LineSession>();
    private readonly List<GameObject> _sessionPois = new List<GameObject>();
    private readonly Dictionary<int, int> _secondaryMenuSlotByIndex = new Dictionary<int, int>();
    private readonly Dictionary<int, MixedReality.Toolkit.StatefulInteractable> _secondaryMenuToggleByIndex = new Dictionary<int, MixedReality.Toolkit.StatefulInteractable>();
    private readonly bool[] _secondaryMenuSlotOccupied = new bool[3];
    private readonly Stack<int> _secondaryMenuHistory = new Stack<int>();
    private bool _secondaryMenuTogglesResolved;

    private IAnnotationPersistenceService _persistence;
    private bool _emptyMapLayerDiagnosticLogged;
    private bool _missingXrRayDiagnosticLogged;
    private bool _missingPointPrefabDiagnosticLogged;
    private bool _missingLineNodePrefabDiagnosticLogged;
    private bool _missingLineMaterialDiagnosticLogged;

#if UNITY_EDITOR || UNITY_STANDALONE
    private Ray? _keyboardDebugPlacementRay;
    private bool _missingPcDebugCameraDiagnosticLogged;
#endif

    public void InjectDependencies(XRControllerInputBridge injectedInput, IAnnotationPersistenceService injectedPersistence)
    {
        inputBridge = injectedInput;
        _persistence = injectedPersistence;
    }

    private void Awake()
    {
        Physics.queriesHitBackfaces = true;

        _persistence = geoJsonPersistenceProvider;

        if (inputBridge == null)
            inputBridge = GetComponent<XRControllerInputBridge>();

        if (inputBridge == null)
            Debug.LogWarning("[XRControllerLogger] Input bridge is missing; Quest/XR controller input is unavailable.", this);

        if (_persistence == null)
            Debug.LogWarning("[XRControllerLogger] Persistence adapter is missing; annotations may not be saved.", this);

        ApplyState(currentState);
    }

    private void OnEnable()
    {
        if (inputBridge == null)
            return;

        inputBridge.RightPrimaryPressed += HandlePlaceAction;
        inputBridge.RightGripPressed += HandleUndoAction;
        inputBridge.LeftGripPressed += HandleFinalizeLineAction;
    }

    private void OnDisable()
    {
        if (inputBridge != null)
        {
            inputBridge.RightPrimaryPressed -= HandlePlaceAction;
            inputBridge.RightGripPressed -= HandleUndoAction;
            inputBridge.LeftGripPressed -= HandleFinalizeLineAction;
        }

        CommitCurrentLineIfNeeded();
    }

#if UNITY_EDITOR || UNITY_STANDALONE
    private void Update()
    {
        if (!enableKeyboardDebugInput)
            return;

        HandleKeyboardDebugInput();
    }

    private void HandleKeyboardDebugInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            HandleCancelDrawingAction();
            return;
        }

        if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
        {
            HandleFinalizeLineAction();
            return;
        }

        if (keyboard.backspaceKey.wasPressedThisFrame)
        {
            HandleUndoAction();
            return;
        }

        if (keyboard.pKey.wasPressedThisFrame)
            TryExecuteKeyboardPlacement(InteractionState.DrawingSinglePoint);
        else if (keyboard.lKey.wasPressedThisFrame)
            TryExecuteKeyboardPlacement(InteractionState.DrawingLine);
        else if (keyboard.oKey.wasPressedThisFrame)
            TryExecuteKeyboardPlacement(InteractionState.DrawingPolygon);
    }

    private void TryExecuteKeyboardPlacement(InteractionState requestedState)
    {
        if (currentState != InteractionState.DrawingSinglePoint &&
            currentState != InteractionState.DrawingLine &&
            currentState != InteractionState.DrawingPolygon)
        {
            Debug.LogWarning("[XRControllerLogger] Keyboard placement ignored because Draw mode is not active.");
            return;
        }

        bool activeSessionMatchesRequestedTool = _currentLine == null || _currentLine.IsSaved ||
            (currentState == requestedState &&
             ((_currentLine.IsPolygon && requestedState == InteractionState.DrawingPolygon) ||
              (!_currentLine.IsPolygon && requestedState == InteractionState.DrawingLine)));

        if (!activeSessionMatchesRequestedTool)
        {
            Debug.LogWarning("[XRControllerLogger] Finish or cancel the active line/polygon before switching keyboard drawing tools.");
            return;
        }

        if (!TryGetKeyboardPointerRay(out Ray pointerRay))
            return;

        switch (requestedState)
        {
            case InteractionState.DrawingSinglePoint:
                SelectSinglePointMode();
                break;
            case InteractionState.DrawingLine:
                SelectLineDrawingMode();
                break;
            case InteractionState.DrawingPolygon:
                SelectPolygonDrawingMode();
                break;
        }

        _keyboardDebugPlacementRay = pointerRay;
        try
        {
            HandlePlaceAction();
        }
        finally
        {
            _keyboardDebugPlacementRay = null;
        }
    }

    private bool TryGetKeyboardPointerRay(out Ray pointerRay)
    {
        pointerRay = default;

        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            if (!_missingPcDebugCameraDiagnosticLogged)
            {
                Debug.LogError("[XRControllerLogger] Cannot place annotation: Camera.main is missing in PC debug mode.", this);
                _missingPcDebugCameraDiagnosticLogged = true;
            }
            return false;
        }

        _missingPcDebugCameraDiagnosticLogged = false;

        if (Mouse.current != null)
        {
            pointerRay = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            return true;
        }

        pointerRay = new Ray(mainCamera.transform.position, mainCamera.transform.forward);
        return true;
    }
#endif

    public void SetColorRed() { currentColor = DrawColor.Red; selectedPrefabIndex = 0; }
    public void SetColorYellow() { currentColor = DrawColor.Yellow; selectedPrefabIndex = 1; }
    public void SetColorBlue() { currentColor = DrawColor.Blue; selectedPrefabIndex = 2; }

    public void SetPolygonCategorySafe() => currentPolygonCategory = PolygonCategory.Safe;
    public void SetPolygonCategoryUnsafe() => currentPolygonCategory = PolygonCategory.Unsafe;

    public void SetColorFromRainbow(Color picked)
    {
        _customColor = picked;
        currentColor = DrawColor.Custom;
        selectedPrefabIndex = -1;
    }

    public void SetRandomRainbowColor()
    {
        Color picked = UnityEngine.Random.ColorHSV(0f, 1f, 0.75f, 1f, 0.75f, 1f);
        SetColorFromRainbow(picked);
    }

    public void SelectLineDrawingMode() => SetState(InteractionState.DrawingLine);

    public void SelectPolygonDrawingMode() => SetState(InteractionState.DrawingPolygon);

    public void SetPolygonDrawingMode(bool isOn)
    {
        if (isOn)
            SetState(InteractionState.DrawingPolygon);
        else if (currentState == InteractionState.DrawingPolygon)
            SetState(InteractionState.Navigation);
    }

    public void SelectSinglePointMode() => SetState(InteractionState.DrawingSinglePoint);

    public void ToggleAnnotationMode()
    {
        SetState(currentState == InteractionState.Navigation
            ? InteractionState.DrawingLine
            : InteractionState.Navigation);
    }

    public void FinalizeCurrentLine() => CommitCurrentLineIfNeeded();

    public void SetSelectedPrefabIndex(int index)
    {
        selectedPrefabIndex = index;
        Debug.Log($"[XRControllerLogger] Selected prefab index: {index}");
    }

    public void ExportAnnotationsToGeoJson()
    {
        CommitCurrentLineIfNeeded();
        Debug.Log("[XRControllerLogger] Export now runs through persistence service at save time.");
    }

    private void SetState(InteractionState newState)
    {
        if (newState == currentState)
            return;

        if ((currentState == InteractionState.DrawingLine || currentState == InteractionState.DrawingPolygon) &&
            newState != InteractionState.DrawingLine && newState != InteractionState.DrawingPolygon)
        {
            CommitCurrentLineIfNeeded();
            _undoStack.Clear();
        }

        currentState = newState;
        ApplyState(currentState);
        Debug.Log($"[XRControllerLogger] State => {currentState}");
    }

    private void ApplyState(InteractionState state)
    {
        bool navigationEnabled = state == InteractionState.Navigation;
        if (mapBoxCollider != null && !navigationEnabled)
            mapBoxCollider.enabled = false;
        if (objectManipulator != null)
            objectManipulator.enabled = navigationEnabled;
    }

    private void HandlePlaceAction()
    {
        if (currentState != InteractionState.DrawingSinglePoint &&
            currentState != InteractionState.DrawingLine &&
            currentState != InteractionState.DrawingPolygon)
            return;

        if (!TryGetAnnotationPlacement(out Vector3 pos, out Quaternion rot))
            return;

        if (currentState == InteractionState.DrawingSinglePoint)
        {
            PlacePoiInternal(pos, rot);
            return;
        }

        ExecuteLineNodeCommand(new AddLineNodeCommand(this, pos));
    }

    private void HandleUndoAction()
    {
        if (currentState != InteractionState.DrawingLine &&
            currentState != InteractionState.DrawingPolygon)
            return;

        if (_currentLine == null || _currentLine.IsSaved)
            return;

        if (_undoStack.Count == 0)
            return;

        AddLineNodeCommand command = _undoStack.Peek();
        if (!command.TargetsLine(_currentLine))
            return;

        _undoStack.Pop();
        command.Undo();
    }

    private void HandleFinalizeLineAction()
    {
        if (currentState == InteractionState.DrawingLine || currentState == InteractionState.DrawingPolygon)
            CommitCurrentLineIfNeeded();
    }

#if UNITY_EDITOR || UNITY_STANDALONE
    private void HandleCancelDrawingAction()
    {
        if (currentState != InteractionState.DrawingLine && currentState != InteractionState.DrawingPolygon)
            return;

        LineSession activeLine = _currentLine;
        if (activeLine == null || activeLine.IsSaved)
            return;

        CompactDeadNodes(activeLine);
        int activeNodeCount = activeLine.Nodes.Count;
        int matchingCommandCount = 0;

        foreach (AddLineNodeCommand command in _undoStack)
        {
            if (command.TargetsLine(activeLine))
                matchingCommandCount++;
        }

        if (activeNodeCount == 0 || matchingCommandCount != activeNodeCount)
        {
            Debug.LogWarning("[XRControllerLogger] Keyboard cancel skipped because the active unsaved drawing could not be identified safely.");
            return;
        }

        List<AddLineNodeCommand> commandsToUndo = new List<AddLineNodeCommand>(matchingCommandCount);
        Stack<AddLineNodeCommand> retainedCommands = new Stack<AddLineNodeCommand>();

        while (_undoStack.Count > 0)
        {
            AddLineNodeCommand command = _undoStack.Pop();
            if (command.TargetsLine(activeLine))
                commandsToUndo.Add(command);
            else
                retainedCommands.Push(command);
        }

        while (retainedCommands.Count > 0)
            _undoStack.Push(retainedCommands.Pop());

        foreach (AddLineNodeCommand command in commandsToUndo)
            command.Undo();
    }
#endif

    private void ExecuteLineNodeCommand(AddLineNodeCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
    }

    private bool TryGetAnnotationPlacement(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;

        if (mapLayer.value == 0)
        {
            if (!_emptyMapLayerDiagnosticLogged)
            {
                Debug.LogError("[XRControllerLogger] Cannot place annotation: mapLayer is empty.", this);
                _emptyMapLayerDiagnosticLogged = true;
            }
            return false;
        }

        _emptyMapLayerDiagnosticLogged = false;

        Vector3 rayOriginPosition;
        Vector3 rayDirection;

#if UNITY_EDITOR || UNITY_STANDALONE
        if (_keyboardDebugPlacementRay.HasValue)
        {
            Ray debugRay = _keyboardDebugPlacementRay.Value;
            rayOriginPosition = debugRay.origin;
            rayDirection = debugRay.direction;
        }
        else
#endif
        {
            if (rightRayInteractor == null)
            {
                if (!_missingXrRayDiagnosticLogged)
                {
                    Debug.LogError("[XRControllerLogger] Cannot place annotation: XR ray interactor is missing.", this);
                    _missingXrRayDiagnosticLogged = true;
                }
                return false;
            }

            Transform rayOrigin = rightRayInteractor.rayOriginTransform != null
                ? rightRayInteractor.rayOriginTransform
                : rightRayInteractor.transform;

            if (rayOrigin == null)
            {
                if (!_missingXrRayDiagnosticLogged)
                {
                    Debug.LogError("[XRControllerLogger] Cannot place annotation: XR ray origin is missing.", this);
                    _missingXrRayDiagnosticLogged = true;
                }
                return false;
            }

            _missingXrRayDiagnosticLogged = false;

            rayOriginPosition = rayOrigin.position;
            rayDirection = rayOrigin.forward;
        }

        if (!Physics.Raycast(rayOriginPosition, rayDirection, out RaycastHit hit, 100f, mapLayer))
            return false;

        pos = hit.point + hit.normal * baseVerticalOffset;
        rot = Quaternion.FromToRotation(Vector3.up, hit.normal);
        return true;
    }

    private GameObject PlacePoiInternal(Vector3 pos, Quaternion rot)
    {
        GameObject poi = SpawnPoiObject(pos, rot, out string className);

        Vector3 desiredLocalScale = poi.transform.localScale;

        Transform parent = GetAnnotationParent();
        if (parent != null)
        {
            poi.transform.SetParent(parent, true);
            poi.transform.localScale = desiredLocalScale;
        }

        EnsurePositiveScaleForBoxCollider(poi);

        SetupPoiMetadata(poi, className, string.Empty);
        _sessionPois.Add(poi);

        string assignedId = _persistence?.SavePoint(poi, className);
        if (!string.IsNullOrEmpty(assignedId))
            ApplyAssignedPointIdentity(poi, className, assignedId);

        return poi;
    }

    private GameObject SpawnPoiObject(Vector3 pos, Quaternion rot, out string className)
    {
        className = "unknown";
        if (availablePointPrefabs != null
            && selectedPrefabIndex >= 0
            && selectedPrefabIndex < availablePointPrefabs.Length
            && availablePointPrefabs[selectedPrefabIndex] != null)
        {
            _missingPointPrefabDiagnosticLogged = false;
            GameObject prefab = availablePointPrefabs[selectedPrefabIndex];
            className = prefab.name.ToLower().Replace(" prefab", string.Empty).Replace("prefab", string.Empty).Trim();
            return Instantiate(prefab, pos, rot);
        }

        if (!_missingPointPrefabDiagnosticLogged)
        {
            Debug.LogWarning("[XRControllerLogger] Selected point prefab is missing; using the existing primitive fallback.", this);
            _missingPointPrefabDiagnosticLogged = true;
        }

        GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        fallback.transform.SetPositionAndRotation(pos, rot);
        fallback.transform.localScale = Vector3.one * 0.05f;
        Renderer renderer = fallback.GetComponent<Renderer>();
        if (renderer != null)
            renderer.material.color = GetCurrentColor();

        Collider collider = fallback.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        return fallback;
    }

    private static void EnsurePositiveScaleForBoxCollider(GameObject obj)
    {
        if (obj == null)
            return;

        BoxCollider[] colliders = obj.GetComponentsInChildren<BoxCollider>(true);
        if (colliders == null || colliders.Length == 0)
            return;

        Vector3 local = obj.transform.localScale;
        Vector3 safe = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

        if (!Mathf.Approximately(local.x, safe.x) || !Mathf.Approximately(local.y, safe.y) || !Mathf.Approximately(local.z, safe.z))
            obj.transform.localScale = safe;
    }

    private void SetupPoiMetadata(GameObject poi, string className, string pointId)
    {
        foreach (Collider col in poi.GetComponentsInChildren<Collider>(true))
            col.enabled = true;

        if (jsonSpawner == null)
            return;

        Transform parentRef = GetAnnotationParent() ?? jsonSpawner.transform;
        Vector3 localPos = parentRef.InverseTransformPoint(poi.transform.position);
        JsonSpawner.Vector3Double wgs = jsonSpawner.ColmapToWgs84(localPos);

        PointData pointData = poi.GetComponent<PointData>() ?? poi.AddComponent<PointData>();
        pointData.pointClass = className;
        pointData.pointID = pointId ?? string.Empty;
        pointData.category = className;
        pointData.source = "Ground Station";
        pointData.latitude = wgs.lat;
        pointData.longitude = wgs.lon;
        pointData.altitude = wgs.alt;
        pointData.confidence = 1f;

        FloatingIcon icon = poi.GetComponent<FloatingIcon>();
        if (icon != null)
            icon.Setup(parentRef, jsonSpawner);

        if (jsonSpawner.infoPanel == null)
            return;

        PulseEffect pulse = poi.GetComponent<PulseEffect>();
        jsonSpawner.infoPanel.RegisterMarker(pointData, pulse);

        MarkerInteractionRelay relay = poi.GetComponent<MarkerInteractionRelay>() ?? poi.AddComponent<MarkerInteractionRelay>();
        relay.Setup(jsonSpawner.infoPanel);
    }

    private static void ApplyAssignedPointIdentity(GameObject poi, string className, string assignedId)
    {
        if (poi == null || string.IsNullOrEmpty(assignedId))
            return;

        PointData pointData = poi.GetComponent<PointData>() ?? poi.AddComponent<PointData>();
        pointData.pointID = assignedId;

        if (!string.IsNullOrWhiteSpace(className))
        {
            pointData.pointClass = className;
            pointData.category = className;
            poi.name = $"{className}_{assignedId}";
        }
    }

    private LineSession GetOrCreateCurrentLine()
    {
        RemoveDeadLineSessions();

        if (IsLineSessionAlive(_currentLine))
            return _currentLine;

        _currentLine = null;
        _currentLine = CreateLineSession();
        _lineSessions.Add(_currentLine);
        return _currentLine;
    }

    private bool IsLineSessionAlive(LineSession line)
    {
        return line != null &&
               line.Renderer != null &&
               line.RuntimeLine != null &&
               line.Renderer.gameObject != null &&
               line.RuntimeLine.gameObject != null;
    }

    private void RemoveDeadLineSessions()
    {
        for (int i = _lineSessions.Count - 1; i >= 0; i--)
        {
            LineSession session = _lineSessions[i];
            if (IsLineSessionAlive(session))
                continue;

            if (_currentLine == session)
                _currentLine = null;

            _lineSessions.RemoveAt(i);
        }
    }

    private LineSession CreateLineSession()
    {
        Color color = GetCurrentColor();
        bool polygonMode = currentState == InteractionState.DrawingPolygon;
        var strokeGo = new GameObject($"Stroke_{(polygonMode ? "Polygon" : "Line")}_{currentColor}_{_lineSessions.Count}");

        Transform parent = GetAnnotationParent();
        if (parent != null)
            strokeGo.transform.SetParent(parent, true);

        LineRenderer lineRenderer = strokeGo.AddComponent<LineRenderer>();
        if (lineMaterial == null && !_missingLineMaterialDiagnosticLogged)
        {
            Debug.LogWarning("[XRControllerLogger] Line material is missing; using the existing Unlit/Color fallback.", this);
            _missingLineMaterialDiagnosticLogged = true;
        }
        else if (lineMaterial != null)
        {
            _missingLineMaterialDiagnosticLogged = false;
        }

        lineRenderer.material = lineMaterial != null
            ? new Material(lineMaterial)
            : new Material(Shader.Find("Unlit/Color"));

        lineRenderer.material.color = color;
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.useWorldSpace = true;
        lineRenderer.loop = polygonMode;

        RuntimeLine runtimeLine = strokeGo.AddComponent<RuntimeLine>();
        runtimeLine.Initialize(lineRenderer);

        return new LineSession
        {
            Renderer = lineRenderer,
            RuntimeLine = runtimeLine,
            IsPolygon = polygonMode,
            StrokeColor = color
        };
    }

    private Transform CreateLineNode(LineSession line, Vector3 worldPos)
    {
        if (!IsLineSessionAlive(line))
        {
            Debug.LogWarning("[XRControllerLogger] Skipped CreateLineNode because line session is no longer valid.");
            return null;
        }

        CompactDeadNodes(line);

        GameObject node;
        Transform parent = GetAnnotationParent() ?? transform;
        int nodeIndex = line.Nodes.Count;

        if (lineNodePrefab != null)
        {
            _missingLineNodePrefabDiagnosticLogged = false;
            node = Instantiate(lineNodePrefab, worldPos, Quaternion.identity, parent);
        }
        else
        {
            if (!_missingLineNodePrefabDiagnosticLogged)
            {
                Debug.LogWarning("[XRControllerLogger] Line node prefab is missing; using the existing empty-node fallback for line and polygon drawing.", this);
                _missingLineNodePrefabDiagnosticLogged = true;
            }

            node = new GameObject("LineNode");
            node.transform.SetParent(parent, true);
            node.transform.position = worldPos;
        }

        node.name = $"Node_{line.Renderer.name}_{nodeIndex}";
        line.Nodes.Add(node.transform);
        line.RuntimeLine.AddNode(node.transform);
        line.RuntimeLine.RefreshNow();
        return node.transform;
    }

    private void RemoveLineNode(LineSession line, Transform node)
    {
        if (line == null || node == null)
            return;

        CompactDeadNodes(line);

        if (line.RuntimeLine != null)
        {
            line.RuntimeLine.RemoveNode(node);
            line.RuntimeLine.RefreshNow();
        }

        line.Nodes.Remove(node);
        Destroy(node.gameObject);

        CompactDeadNodes(line);

        if (line.Nodes.Count > 0)
            return;

        if (_currentLine == line)
            _currentLine = null;

        _lineSessions.Remove(line);
        if (line.Renderer != null)
            Destroy(line.Renderer.gameObject);
    }

    private void CommitCurrentLineIfNeeded()
    {
        LineSession activeLine = _currentLine;
        if (activeLine == null)
            return;

        if (activeLine.IsSaved)
        {
            RemoveUndoCommandsForSession(activeLine);
            return;
        }

        if (!IsLineSessionAlive(activeLine))
        {
            RemoveUndoCommandsForSession(activeLine);
            _lineSessions.Remove(activeLine);
            _currentLine = null;
            return;
        }

        CompactDeadNodes(activeLine);
        if (activeLine.RuntimeLine != null)
            activeLine.RuntimeLine.RefreshNow();

        List<Vector3> positions = new List<Vector3>(activeLine.Nodes.Count);
        foreach (Transform node in activeLine.Nodes)
            if (node != null)
                positions.Add(node.position);

        bool polygonMode = activeLine.IsPolygon;
        if (polygonMode ? positions.Count >= 3 : positions.Count >= 2)
        {
            Color lineColor = activeLine.StrokeColor;
            string featureClass = BuildDrawingClassName(lineColor);
            string polygonClass = currentPolygonCategory == PolygonCategory.Unsafe ? "unsafe" : "safe";
            string polygonCategory = "navigation";
            string featureId = polygonMode
                ? _persistence?.SavePolygon(positions, polygonClass, polygonCategory, lineColor)
                : _persistence?.SaveLine(positions, featureClass, lineColor);

            if (!string.IsNullOrEmpty(featureId))
            {
                if (polygonMode)
                    _persistence.RegisterRuntimePolygonNodes(featureId, activeLine.Nodes);
                else
                    _persistence.RegisterRuntimeLineNodes(featureId, activeLine.Nodes);

                RegisterLineNodesToInfoPanel(activeLine, featureId);
            }
        }

        activeLine.IsSaved = true;
        RemoveUndoCommandsForSession(activeLine);
        _currentLine = null;
    }

    private void RemoveUndoCommandsForSession(LineSession line)
    {
        if (line == null || _undoStack.Count == 0)
            return;

        Stack<AddLineNodeCommand> retainedCommands = new Stack<AddLineNodeCommand>();
        while (_undoStack.Count > 0)
        {
            AddLineNodeCommand command = _undoStack.Pop();
            if (!command.TargetsLine(line))
                retainedCommands.Push(command);
        }

        while (retainedCommands.Count > 0)
            _undoStack.Push(retainedCommands.Pop());
    }

    private static void CompactDeadNodes(LineSession line)
    {
        if (line == null)
            return;

        line.Nodes.RemoveAll(node => node == null);
    }

    private string BuildDrawingClassName(Color color)
    {
        string label = GetCurrentColorLabel(color);
        if (string.IsNullOrWhiteSpace(label))
            label = currentColor.ToString();

        string normalized = label.Trim().Replace(" ", "_").Replace("-", "_");
        return $"Drawing_{normalized}";
    }

    private void RegisterLineNodesToInfoPanel(LineSession line, string featureId)
    {
        if (jsonSpawner == null || jsonSpawner.infoPanel == null)
            return;

        Color lineColor = line != null && line.Renderer != null ? line.Renderer.startColor : GetCurrentColor();

        Transform parentRef = GetAnnotationParent() ?? transform;
        foreach (Transform node in line.Nodes)
        {
            if (node == null)
                continue;

            PointData pd = node.GetComponent<PointData>() ?? node.gameObject.AddComponent<PointData>();
            Vector3 localPos = parentRef.InverseTransformPoint(node.position);
            JsonSpawner.Vector3Double wgs = jsonSpawner.ColmapToWgs84(localPos);

            pd.lineColorHex = "#" + ColorUtility.ToHtmlStringRGB(lineColor);
            pd.pointClass = line != null && line.IsPolygon
                ? (currentPolygonCategory == PolygonCategory.Unsafe ? "unsafe" : "safe")
                : "road";
            pd.pointID = featureId;
            pd.category = "navigation";
            pd.source = "Ground Station";
            pd.latitude = wgs.lat;
            pd.longitude = wgs.lon;
            pd.altitude = wgs.alt;
            pd.confidence = 1f;

            node.gameObject.tag = "MapMarker";
            Collider nodeCollider = node.GetComponent<Collider>();
            if (nodeCollider == null)
            {
                SphereCollider fallbackCollider = node.gameObject.AddComponent<SphereCollider>();
                fallbackCollider.radius = Mathf.Max(0.01f, lineWidth * 2f);
            }

            foreach (Collider childCollider in node.GetComponentsInChildren<Collider>(true))
                childCollider.enabled = true;

            if (node.GetComponent<XRBaseInteractable>() == null)
                node.gameObject.AddComponent<XRSimpleInteractable>();

            FloatingIcon icon = node.GetComponent<FloatingIcon>();
            if (icon != null)
                icon.Setup(parentRef, jsonSpawner);

            PulseEffect pulse = node.GetComponent<PulseEffect>() ?? node.GetComponentInChildren<PulseEffect>(true);
            jsonSpawner.infoPanel.RegisterMarker(pd, pulse);

            MarkerInteractionRelay relay = node.GetComponent<MarkerInteractionRelay>() ?? node.gameObject.AddComponent<MarkerInteractionRelay>();
            relay.Setup(jsonSpawner.infoPanel);
        }
    }

    private Color GetCurrentColor()
    {
        return currentColor switch
        {
            DrawColor.Red => colorRed,
            DrawColor.Yellow => colorYellow,
            DrawColor.Blue => colorBlue,
            DrawColor.Custom => _customColor,
            _ => colorRed
        };
    }

    private string GetCurrentColorLabel(Color color)
    {
        if (ApproximatelyColor(color, colorRed)) return "Red";
        if (ApproximatelyColor(color, colorYellow)) return "Yellow";
        if (ApproximatelyColor(color, colorBlue)) return "Blue";

        return GetNearestNamedColor(color);
    }

    private static string GetNearestNamedColor(Color color)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);

        if (v < 0.15f) return "Black";
        if (s < 0.12f)
        {
            if (v > 0.88f) return "White";
            return "Gray";
        }

        float hue = h * 360f;
        if (hue < 15f || hue >= 345f) return "Red";
        if (hue < 40f) return "Orange";
        if (hue < 70f) return "Yellow";
        if (hue < 105f) return "Lime";
        if (hue < 165f) return "Green";
        if (hue < 200f) return "Cyan";
        if (hue < 255f) return "Blue";
        if (hue < 285f) return "Purple";
        if (hue < 330f) return "Magenta";
        return "Pink";
    }

    private static bool ApproximatelyColor(Color a, Color b)
    {
        const float tolerance = 0.08f;
        return Mathf.Abs(a.r - b.r) <= tolerance &&
               Mathf.Abs(a.g - b.g) <= tolerance &&
               Mathf.Abs(a.b - b.b) <= tolerance;
    }

    private Transform GetAnnotationParent()
    {
        if (annotationParent != null)
            return annotationParent;
        if (mapBoxCollider != null)
            return mapBoxCollider.transform;
        return null;
    }

    private void ShowMenu()
    {
        if (rootHandMenu == null)
            return;

        Camera mainCam = Camera.main;
        Transform camTransform = headTransform != null ? headTransform : (mainCam != null ? mainCam.transform : null);
        if (camTransform == null)
            return;

        Vector3 spawnPos = camTransform.position + camTransform.forward * spawnDistanceFromHead;
        spawnPos.y += spawnHeightOffset;

        Vector3 dir = camTransform.position - spawnPos;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -camTransform.forward;
        if (keepMenuVertical && !faceEyesWhenLookingUpDown)
            dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            dir = Vector3.forward;

        Quaternion spawnRot = Quaternion.LookRotation(dir.normalized, Vector3.up)
                              * Quaternion.Euler(additionalRotationOffset);

        rootHandMenu.transform.position = spawnPos;
        rootHandMenu.transform.rotation = spawnRot;
        rootHandMenu.SetActive(true);

        if (GetActiveSidePanelIndex() >= 0 || (menuContentCanvas != null && menuContentCanvas.activeSelf))
            return;

        if (menuContentCanvas != null)
            menuContentCanvas.SetActive(true);
        if (closeButton != null)
            closeButton.SetActive(true);
    }

    public void RecenterMenuNearUser()
    {
        ShowMenu();
        RepositionOpenSecondaryMenus();
    }

    public void ToggleSecondaryMenu(int menuIndex, bool isToggled)
    {
        if (secondaryMenus == null || menuIndex < 0 || menuIndex >= secondaryMenus.Length)
            return;

        SecondaryMenuEntry entry = secondaryMenus[menuIndex];
        if (entry == null || entry.menu == null)
            return;

        if (!isToggled)
        {
            bool wasActive = entry.menu.activeSelf;
            CloseSecondaryMenuInternal(menuIndex);

            if (wasActive && GetOpenSecondaryMenuIndexExcept(menuIndex) < 0)
                RestorePreviousSecondaryMenu(menuIndex);

            return;
        }

        CloseConflictingSecondaryMenus(menuIndex);

        entry.menu.SetActive(true);
        SynchronizeSecondaryMenuToggleState(menuIndex, true);
        if (!_secondaryMenuSlotByIndex.TryGetValue(menuIndex, out int assignedSlot))
        {
            assignedSlot = GetFirstFreeSecondarySlot();
            if (assignedSlot < 0)
                assignedSlot = _secondaryMenuSlotOccupied.Length - 1;

            _secondaryMenuSlotByIndex[menuIndex] = assignedSlot;
            _secondaryMenuSlotOccupied[assignedSlot] = true;
        }

        PlaceSecondaryMenuAtSlot(menuIndex, assignedSlot);
    }

    public void ToggleSecondaryMenu0(bool isToggled) => ToggleSecondaryMenu(0, isToggled);
    public void ToggleSecondaryMenu1(bool isToggled) => ToggleSecondaryMenu(1, isToggled);
    public void ToggleSecondaryMenu2(bool isToggled) => ToggleSecondaryMenu(2, isToggled);

    private void CloseSecondaryMenuInternal(int menuIndex)
    {
        if (secondaryMenus == null || menuIndex < 0 || menuIndex >= secondaryMenus.Length)
            return;

        SecondaryMenuEntry entry = secondaryMenus[menuIndex];
        if (entry == null || entry.menu == null)
            return;

        entry.menu.SetActive(false);
        SynchronizeSecondaryMenuToggleState(menuIndex, false);
        if (_secondaryMenuSlotByIndex.TryGetValue(menuIndex, out int slotIndex))
        {
            _secondaryMenuSlotByIndex.Remove(menuIndex);
            if (slotIndex >= 0 && slotIndex < _secondaryMenuSlotOccupied.Length)
                _secondaryMenuSlotOccupied[slotIndex] = false;
        }
    }

    private int GetOpenSecondaryMenuIndexExcept(int excludedIndex)
    {
        if (secondaryMenus == null)
            return -1;

        for (int i = 0; i < secondaryMenus.Length; i++)
        {
            if (i == excludedIndex)
                continue;

            SecondaryMenuEntry entry = secondaryMenus[i];
            if (entry != null && entry.menu != null && entry.menu.activeSelf)
                return i;
        }

        return -1;
    }

    private void CloseConflictingSecondaryMenus(int openingMenuIndex)
    {
        for (int i = 0; i < secondaryMenus.Length; i++)
        {
            if (i == openingMenuIndex || CanSecondaryMenusCoexist(openingMenuIndex, i))
                continue;

            SecondaryMenuEntry entry = secondaryMenus[i];
            if (entry == null || entry.menu == null || !entry.menu.activeSelf)
                continue;

            PushSecondaryHistory(i);
            CloseSecondaryMenuInternal(i);
        }
    }

    private static bool CanSecondaryMenusCoexist(int firstMenuIndex, int secondMenuIndex)
    {
        return (firstMenuIndex == FiltersSecondaryMenuIndex && secondMenuIndex == InfoPanelSecondaryMenuIndex)
            || (firstMenuIndex == InfoPanelSecondaryMenuIndex && secondMenuIndex == FiltersSecondaryMenuIndex);
    }

    private void SynchronizeSecondaryMenuToggleState(int menuIndex, bool isOpen)
    {
        if (!_secondaryMenuTogglesResolved)
            ResolveSecondaryMenuToggles();

        if (!_secondaryMenuToggleByIndex.TryGetValue(menuIndex, out MixedReality.Toolkit.StatefulInteractable toggle) || toggle == null)
            return;

        if (toggle.IsToggled.Active != isOpen)
            toggle.ForceSetToggled(isOpen, fireEvents: false);
    }

    private void ResolveSecondaryMenuToggles()
    {
        _secondaryMenuTogglesResolved = true;
        _secondaryMenuToggleByIndex.Clear();

        if (rootHandMenu == null || secondaryMenus == null)
            return;

        MixedReality.Toolkit.StatefulInteractable[] toggles =
            rootHandMenu.GetComponentsInChildren<MixedReality.Toolkit.StatefulInteractable>(true);

        foreach (MixedReality.Toolkit.StatefulInteractable toggle in toggles)
        {
            if (toggle == null)
                continue;

            for (int menuIndex = 0; menuIndex < secondaryMenus.Length; menuIndex++)
            {
                if (_secondaryMenuToggleByIndex.ContainsKey(menuIndex))
                    continue;

                string callbackName = GetSecondaryMenuToggleCallbackName(menuIndex);
                if (!string.IsNullOrEmpty(callbackName) && HasPersistentToggleCallback(toggle, callbackName))
                    _secondaryMenuToggleByIndex[menuIndex] = toggle;
            }
        }
    }

    private bool HasPersistentToggleCallback(MixedReality.Toolkit.StatefulInteractable toggle, string callbackName)
    {
        return HasPersistentCallback(toggle.IsToggled.OnEntered, callbackName) ||
               HasPersistentCallback(toggle.IsToggled.OnExited, callbackName);
    }

    private bool HasPersistentCallback(UnityEngine.Events.UnityEventBase toggleEvent, string callbackName)
    {
        for (int i = 0; i < toggleEvent.GetPersistentEventCount(); i++)
        {
            if (toggleEvent.GetPersistentTarget(i) == this &&
                string.Equals(toggleEvent.GetPersistentMethodName(i), callbackName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetSecondaryMenuToggleCallbackName(int menuIndex)
    {
        return menuIndex switch
        {
            0 => nameof(ToggleSecondaryMenu0),
            1 => nameof(ToggleSecondaryMenu1),
            2 => nameof(ToggleSecondaryMenu2),
            _ => string.Empty
        };
    }

    private void PushSecondaryHistory(int menuIndex)
    {
        if (menuIndex < 0)
            return;

        if (_secondaryMenuHistory.Count > 0 && _secondaryMenuHistory.Peek() == menuIndex)
            return;

        _secondaryMenuHistory.Push(menuIndex);
    }

    private void RestorePreviousSecondaryMenu(int closedMenuIndex)
    {
        while (_secondaryMenuHistory.Count > 0)
        {
            int previous = _secondaryMenuHistory.Pop();
            if (previous < 0 || previous == closedMenuIndex)
                continue;

            if (secondaryMenus == null || previous >= secondaryMenus.Length)
                continue;

            SecondaryMenuEntry entry = secondaryMenus[previous];
            if (entry == null || entry.menu == null)
                continue;

            ToggleSecondaryMenu(previous, true);
            return;
        }
    }

    private int GetFirstFreeSecondarySlot()
    {
        for (int i = 0; i < _secondaryMenuSlotOccupied.Length; i++)
            if (!_secondaryMenuSlotOccupied[i])
                return i;
        return -1;
    }

    private Vector3 GetSecondarySlotOffset(int slotIndex)
    {
        return slotIndex switch
        {
            0 => slotOffsetRight,
            1 => slotOffsetLeft,
            2 => slotOffsetTop,
            _ => slotOffsetTop
        };
    }

    private void PlaceSecondaryMenuAtSlot(int menuIndex, int slotIndex)
    {
        Transform reference = secondaryMenuReferencePoint != null
            ? secondaryMenuReferencePoint
            : (rootHandMenu != null ? rootHandMenu.transform : null);
        if (reference == null)
            return;

        Camera mainCam = Camera.main;
        Transform camTransform = headTransform != null ? headTransform : (mainCam != null ? mainCam.transform : null);
        if (camTransform == null)
            return;

        SecondaryMenuEntry entry = secondaryMenus[menuIndex];
        if (entry == null || entry.menu == null || !entry.menu.activeSelf)
            return;

        Vector3 spawnPos = reference.TransformPoint(GetSecondarySlotOffset(slotIndex));
        Vector3 dir = camTransform.position - spawnPos;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -camTransform.forward;
        if (keepSecondaryMenusVertical)
            dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            dir = Vector3.forward;

        Quaternion spawnRot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        if (flipSecondaryMenusFacing)
            spawnRot *= Quaternion.Euler(0f, 180f, 0f);
        spawnRot *= Quaternion.Euler(secondaryMenusRotationOffset);

        entry.menu.transform.position = spawnPos;
        entry.menu.transform.rotation = spawnRot;
        if (entry.radialView != null)
            entry.radialView.enabled = true;
    }

    private void RepositionOpenSecondaryMenus()
    {
        foreach (var pair in _secondaryMenuSlotByIndex)
            PlaceSecondaryMenuAtSlot(pair.Key, pair.Value);
    }

    private int GetActiveSidePanelIndex()
    {
        if (sidePanels != null)
        {
            for (int i = 0; i < sidePanels.Length; i++)
            {
                if (sidePanels[i] != null && sidePanels[i].activeSelf)
                    return i;
            }
        }

        return -1;
    }
}
