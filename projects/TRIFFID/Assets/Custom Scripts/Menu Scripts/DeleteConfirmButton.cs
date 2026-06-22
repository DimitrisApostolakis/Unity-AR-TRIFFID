using UnityEngine;
using TMPro;
using System.Collections;
using UnityEngine.Events;

public class DeleteConfirmButton : MonoBehaviour
{
    public Transform targetNode;

    public TextMeshProUGUI buttonText; 

    [Header("Text")]
    public string defaultText = "Delete";
    public string confirmText = "Sure?";

    [Header("Colors")]
    public Color defaultColor = Color.white;
    public Color confirmColor = new Color32(0xAC, 0x22, 0x22, 0xFF);
    public Color confirmHoverColor = new Color32(0x2A, 0xBE, 0x2A, 0xFF);
    
    public float cancelDelay = 3f; 

    [Header("Events")]
    [SerializeField] private UnityEvent onConfirmDelete;

    private bool isConfirming = false;
    private bool isHovered    = false; 
    private Coroutine cancelCoroutine;

    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable interactable;

    private void Start()
    {
        interactable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>();
        ValidateConfiguration();
        if (interactable != null)
        {
            interactable.firstHoverEntered.AddListener(evt => OnHoverEntered());
            interactable.lastHoverExited.AddListener(evt => OnHoverExited());
        }

        if (buttonText != null)
            buttonText.richText = true;

        ResetButton();
    }

    private void ValidateConfiguration()
    {
        if (buttonText == null)
            Debug.LogWarning("[DeleteConfirmButton] Button text is missing; confirmation state will not be visible.", this);

        if (interactable == null)
            Debug.LogWarning("[DeleteConfirmButton] XR interactable is missing; hover color feedback is unavailable.", this);

        if (!IsFinite(cancelDelay) || cancelDelay < 0f)
            Debug.LogWarning("[DeleteConfirmButton] cancelDelay must be finite and non-negative.", this);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnDisable()
    {
        if (cancelCoroutine != null)
        {
            StopCoroutine(cancelCoroutine);
            cancelCoroutine = null;
        }

        ResetButton();
    }

    private void LateUpdate()
    {
        if (isConfirming && targetNode == null)
            ResetButton();
    }

    public void SetTargetNode(Transform newNode)
    {
        targetNode = newNode;
        if (cancelCoroutine != null) { StopCoroutine(cancelCoroutine); cancelCoroutine = null; }
        ResetButton(); 
    }

    private void OnHoverEntered()
    {
        isHovered = true;
        UpdateUI();
    }

    private void OnHoverExited()
    {
        isHovered = false;
        UpdateUI();
    }

    private void UpdateUI()
    {
        if (buttonText == null) return;

        string currentText  = isConfirming ? confirmText : defaultText;
        Color currentColor = defaultColor;

        if (isConfirming)
            currentColor = isHovered ? confirmHoverColor : confirmColor;

        string colorHex = $"#{ColorUtility.ToHtmlStringRGBA(currentColor)}";
        buttonText.text = $"<color={colorHex}>{currentText}</color>";
    }

    public void OnDeletePressed()
    {
        if (targetNode == null) 
        {
            PointData parentData = GetComponentInParent<PointData>();
            if (parentData != null) 
                targetNode = parentData.transform;
            else
            {
                Debug.LogWarning("[DeleteConfirmButton] No target node to delete.");
                ResetButton();
                return;
            }
        }

        if (!isConfirming)
        {
            isConfirming = true;
            UpdateUI(); 

            if (cancelCoroutine != null) StopCoroutine(cancelCoroutine);
            cancelCoroutine = StartCoroutine(CancelConfirmation());
        }
        else
        {
            if (cancelCoroutine != null) { StopCoroutine(cancelCoroutine); cancelCoroutine = null; }

            Transform nodeToDelete = targetNode;
            targetNode = null;
            isConfirming = false;

            if (nodeToDelete != null)
            {
                onConfirmDelete?.Invoke();
            }

            ResetButton();
        }
    }

    private IEnumerator CancelConfirmation()
    {
        yield return new WaitForSeconds(cancelDelay);
        ResetButton(); 
    }

    private void ResetButton()
    {
        isConfirming = false;
        UpdateUI(); 
    }
}
