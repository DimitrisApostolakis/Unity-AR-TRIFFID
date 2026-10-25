using UnityEngine;

public class CameraBackgroundToggle : MonoBehaviour
{
    [SerializeField] private Camera mainCamera;

    void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;

        if (mainCamera == null)
            Debug.LogWarning("[CameraBackgroundToggle] No camera is assigned and Camera.main was not found.", this);

        if (mainCamera != null)
        {
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
        }
    }

    public void SetCameraBackground(bool isToggled)
    {
        if (mainCamera == null)
        {
            return;
        }

        if (isToggled)
        {
            mainCamera.backgroundColor = new Color(0f, 0f, 0f, 1f);
        }
        else
        {

            mainCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }
    }
}
