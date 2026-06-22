using UnityEngine;

public class FreeLocomotion : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 2f;
    public float verticalSpeed = 2f;

    [Header("Snap Turn Settings")]
    public Transform snapTurnRoot;     
    public bool rotateAroundCamera = false; 
    public float snapCooldown = 0.3f;  

    [Header("Input")]
    [SerializeField] private XRControllerInputBridge inputBridge;

    private float snapTimer = 0f;
    private bool _snapRequested;
    private Vector2 _leftInput;
    private Vector2 _rightInput;

    [Header("Rotation Settings")]
    [SerializeField] private float rotationDegrees = 90f;

    private void Awake()
    {
        if (inputBridge == null)
            inputBridge = GetComponent<XRControllerInputBridge>();

        if (inputBridge == null)
            Debug.LogWarning("[FreeLocomotion] XRControllerInputBridge is not assigned. Locomotion input is disabled.");
    }

    private void Start()
    {
        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (!IsFinite(moveSpeed) || !IsFinite(verticalSpeed) || !IsFinite(rotationDegrees))
            Debug.LogWarning("[FreeLocomotion] Movement or rotation configuration contains a non-finite value.", this);

        if (!IsFinite(snapCooldown) || snapCooldown < 0f)
            Debug.LogWarning("[FreeLocomotion] snapCooldown must be finite and non-negative.", this);

        if (Camera.main == null)
            Debug.LogWarning("[FreeLocomotion] Camera.main is missing; camera-relative movement is unavailable.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnEnable()
    {
        if (inputBridge != null)
            inputBridge.RightStickClicked += OnRightStickClicked;
    }

    private void OnDisable()
    {
        if (inputBridge != null)
            inputBridge.RightStickClicked -= OnRightStickClicked;
    }

    private void Update()
    {
        TickSnapCooldown();
        ReadMovementInput();
        ApplyMovement();
        ProcessSnapTurnRequest();
    }

    private void TickSnapCooldown()
    {
        snapTimer -= Time.deltaTime;
    }

    private void ReadMovementInput()
    {
        if (inputBridge == null)
        {
            _leftInput = Vector2.zero;
            _rightInput = Vector2.zero;
            return;
        }

        _leftInput = inputBridge.LeftMoveAxis;
        _rightInput = inputBridge.RightMoveAxis;
    }

    private void ApplyMovement()
    {
        Camera mainCam = Camera.main;
        if (mainCam == null)
            return;

        Vector3 forward = mainCam.transform.forward;
        Vector3 right = mainCam.transform.right;

        forward.y = 0;
        right.y = 0;

        forward.Normalize();
        right.Normalize();

        Vector3 horizontalMove = forward * _leftInput.y + right * _leftInput.x;
        Vector3 verticalMove = Vector3.up * _rightInput.y;

        Vector3 finalMove = horizontalMove * moveSpeed + verticalMove * verticalSpeed;

        transform.position += finalMove * Time.deltaTime;
    }

    private void OnRightStickClicked()
    {
        _snapRequested = true;
    }

    private void ProcessSnapTurnRequest()
    {
        if (!_snapRequested)
            return;

        if (snapTimer <= 0f)
        {
            SnapTurn180();
            snapTimer = snapCooldown;
        }

        _snapRequested = false;
    }

    private void SnapTurn180()
    {
        Transform root = snapTurnRoot != null ? snapTurnRoot : transform;

        if (root == null)
        {
            Debug.LogWarning("[FreeLocomotion] No root transform is available for snap turn.", this);
            return;
        }

        Vector3 pivot = root.position;

        if (rotateAroundCamera && Camera.main != null)
        {
            pivot = Camera.main.transform.position;
        }

        root.RotateAround(pivot, Vector3.up, rotationDegrees);

    }
}
