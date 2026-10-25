using System.Reflection;
using UnityEngine;

public class CheckboxFilterHook : MonoBehaviour
{
    [Tooltip("The CheckboxFilterGroup to notify.")]
    public CheckboxFilterGroup group;

    [Tooltip("The checkbox/interactable component for this item (e.g., UnityEngine.UI.Toggle or MRTK StatefulInteractable).")]
    public Component item;

    private bool missingGroupWarningLogged;
    private bool missingItemWarningLogged;
    private bool invalidItemWarningLogged;

    private void Awake()
    {
        if (group == null)
            LogMissingGroupOnce();

        if (item != null && !HasBoolToggleAPI(item))
            LogInvalidItemOnce(item);
    }

    public void OnPressed()
    {
        if (group == null)
        {
            LogMissingGroupOnce();
            return;
        }
        if (item == null)
        {
            item = DetectItem();
            if (item == null)
            {
                if (!missingItemWarningLogged)
                {
                    Debug.LogWarning("[CheckboxFilterHook] No compatible checkbox/interactable component could be detected.", this);
                    missingItemWarningLogged = true;
                }
                return;
            }
        }

        if (!HasBoolToggleAPI(item))
            LogInvalidItemOnce(item);

        group.OnItemPressed(item);
    }

    [ContextMenu("Detect Item")]
    public void Detect()
    {
        item = DetectItem();
        if (item != null)
        {
            missingItemWarningLogged = false;
            invalidItemWarningLogged = false;
            Debug.Log($"[CheckboxFilterHook] Detected '{item.GetType().Name}'.", this);
        }
        else
            Debug.LogWarning("[CheckboxFilterHook] No compatible component found.", this);
    }

    private void LogMissingGroupOnce()
    {
        if (missingGroupWarningLogged)
            return;

        Debug.LogWarning("[CheckboxFilterHook] CheckboxFilterGroup is not assigned.", this);
        missingGroupWarningLogged = true;
    }

    private void LogInvalidItemOnce(Component configuredItem)
    {
        if (invalidItemWarningLogged)
            return;

        string typeName = configuredItem != null ? configuredItem.GetType().Name : "null";
        Debug.LogWarning($"[CheckboxFilterHook] Assigned item '{typeName}' does not expose a supported boolean toggle API.", this);
        invalidItemWarningLogged = true;
    }

    private Component DetectItem()
    {
        var comps = GetComponents<Component>();
        foreach (var c in comps)
        {
            if (HasBoolToggleAPI(c)) return c;
        }
        return null;
    }

    private static bool HasBoolToggleAPI(Component c)
    {
        if (c == null) return false;
        var t = c.GetType();
        if (t.GetProperty("IsToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(bool)) return true;
        if (t.GetProperty("isOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(bool)) return true;
        if (t.GetProperty("IsOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(bool)) return true;
        if (t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(bool)) return true;
        if (t.GetField("IsToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType == typeof(bool)) return true;
        if (t.GetField("isOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType == typeof(bool)) return true;
        if (t.GetField("IsOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType == typeof(bool)) return true;
        if (t.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType == typeof(bool)) return true;
        if (t.GetMethod("ForceSetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null) != null) return true;
        if (t.GetMethod("SetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null) != null) return true;
        return false;
    }
}
