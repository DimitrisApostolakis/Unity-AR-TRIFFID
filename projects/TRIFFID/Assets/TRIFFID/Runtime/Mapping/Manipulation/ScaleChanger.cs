using UnityEngine;

public class ScaleChanger : MonoBehaviour
{

    public Transform targetObject;

    public Vector3 targetScale = new Vector3(2f, 2f, 2f);
    private bool missingTargetWarningLogged;
    private bool invalidScaleWarningLogged;

    public void ApplyNewScale()
    {
        ValidateForOperation(true);
        if (targetObject != null)
        {
            targetObject.localScale = targetScale;
        }

    }

    public void ResetScale()
    {
        ValidateForOperation(false);
        if (targetObject != null)
        {
            targetObject.localScale = Vector3.one;
        }
    }

    private void ValidateForOperation(bool validateConfiguredScale)
    {
        if (targetObject == null)
        {
            if (!missingTargetWarningLogged)
            {
                Debug.LogWarning("[ScaleChanger] Target transform is missing; scale change cannot be applied.", this);
                missingTargetWarningLogged = true;
            }
        }
        else
        {
            missingTargetWarningLogged = false;
        }

        if (validateConfiguredScale && !IsFinite(targetScale) && !invalidScaleWarningLogged)
        {
            Debug.LogWarning("[ScaleChanger] Target scale contains a non-finite value.", this);
            invalidScaleWarningLogged = true;
        }
        else if (validateConfiguredScale && IsFinite(targetScale))
        {
            invalidScaleWarningLogged = false;
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
