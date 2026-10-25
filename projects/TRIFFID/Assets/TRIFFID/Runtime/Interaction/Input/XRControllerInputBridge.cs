using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class XRControllerInputBridge : MonoBehaviour
{
    public event Action RightPrimaryPressed;
    public event Action RightGripPressed;
    public event Action LeftGripPressed;
    public event Action LeftPrimaryPressed;
    public event Action RightStickClicked;

    public Vector2 LeftMoveAxis => _leftMoveAction != null ? _leftMoveAction.ReadValue<Vector2>() : Vector2.zero;
    public Vector2 RightMoveAxis => _rightMoveAction != null ? _rightMoveAction.ReadValue<Vector2>() : Vector2.zero;

    private InputAction _rightPrimaryAction;
    private InputAction _rightGripAction;
    private InputAction _leftGripAction;
    private InputAction _leftPrimaryAction;
    private InputAction _leftMoveAction;
    private InputAction _rightMoveAction;
    private InputAction _rightStickClickAction;

    private void Awake()
    {
        _rightPrimaryAction = new InputAction("Right_Primary", InputActionType.Button,
            "<XRController>{RightHand}/primaryButton");
        _rightGripAction = new InputAction("Right_Grip", InputActionType.Button,
            "<XRController>{RightHand}/gripPressed");
        _leftGripAction = new InputAction("Left_Grip", InputActionType.Button,
            "<XRController>{LeftHand}/gripPressed");
        _leftPrimaryAction = new InputAction("Left_Primary", InputActionType.Button,
            "<XRController>{LeftHand}/primaryButton");
        _leftMoveAction = new InputAction("Left_Move", InputActionType.Value,
            "<XRController>{LeftHand}/primary2DAxis");
        _rightMoveAction = new InputAction("Right_Move", InputActionType.Value,
            "<XRController>{RightHand}/primary2DAxis");
        _rightStickClickAction = new InputAction("Right_Stick_Click", InputActionType.Button,
            "<XRController>{RightHand}/thumbstickClicked");

        _rightPrimaryAction.performed += OnRightPrimary;
        _rightGripAction.performed += OnRightGrip;
        _leftGripAction.performed += OnLeftGrip;
        _leftPrimaryAction.performed += OnLeftPrimary;
        _rightStickClickAction.performed += OnRightStickClick;
    }

    private void OnEnable()
    {
        _rightPrimaryAction.Enable();
        _rightGripAction.Enable();
        _leftGripAction.Enable();
        _leftPrimaryAction.Enable();
        _leftMoveAction.Enable();
        _rightMoveAction.Enable();
        _rightStickClickAction.Enable();
    }

    private void OnDisable()
    {
        _rightPrimaryAction.Disable();
        _rightGripAction.Disable();
        _leftGripAction.Disable();
        _leftPrimaryAction.Disable();
        _leftMoveAction.Disable();
        _rightMoveAction.Disable();
        _rightStickClickAction.Disable();
    }

    private void OnDestroy()
    {
        _rightPrimaryAction.performed -= OnRightPrimary;
        _rightGripAction.performed -= OnRightGrip;
        _leftGripAction.performed -= OnLeftGrip;
        _leftPrimaryAction.performed -= OnLeftPrimary;
        _rightStickClickAction.performed -= OnRightStickClick;

        _rightPrimaryAction.Dispose();
        _rightGripAction.Dispose();
        _leftGripAction.Dispose();
        _leftPrimaryAction.Dispose();
        _leftMoveAction.Dispose();
        _rightMoveAction.Dispose();
        _rightStickClickAction.Dispose();
    }

    private void OnRightPrimary(InputAction.CallbackContext context) => RightPrimaryPressed?.Invoke();
    private void OnRightGrip(InputAction.CallbackContext context) => RightGripPressed?.Invoke();
    private void OnLeftGrip(InputAction.CallbackContext context) => LeftGripPressed?.Invoke();
    private void OnLeftPrimary(InputAction.CallbackContext context) => LeftPrimaryPressed?.Invoke();
    private void OnRightStickClick(InputAction.CallbackContext context) => RightStickClicked?.Invoke();
}
