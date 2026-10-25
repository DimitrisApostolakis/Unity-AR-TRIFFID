using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public class CheckboxFilterGroup : MonoBehaviour
{
    public Component master;

    public List<Component> items = new List<Component>();

    public bool autoPopulateFromChildren = true;

    public Transform scanRoot;

    public string masterNameHint = "All";

    public bool excludeMasterFromItems = true;

    public bool anyItemControlsAll = false;

    public void OnMasterChanged(bool _)
    {
        OnMasterPressed();
    }

    public void OnMasterPressed()
    {
        if (items == null || items.Count == 0) return;

        bool anyOff = items.Any(i => i != null && !GetState(i));
        bool target = anyOff;

        SetAll(target);
        if (master != null)
        {
            SetState(master, target);
        }
    }

    public void SetAllState(bool value)
    {
        SetAll(value);
        if (master != null)
        {
            SetState(master, value);
        }
    }

    public void UntoggleAll()
    {
        SetAllState(false);
    }

    public void ToggleAllOn()
    {
        SetAllState(true);
    }

    public void OnItemPressed(Component item)
    {
        if (item == null) return;

        if (anyItemControlsAll)
        {
            OnMasterPressed();
        }
        else
        {
            bool current = GetState(item);
            SetState(item, !current);
            UpdateMasterAggregate();
        }
    }

    private void UpdateMasterAggregate()
    {
        if (master == null || items == null || items.Count == 0) return;
        bool allOn = items.All(i => i != null && GetState(i));
        SetState(master, allOn);
    }

    private void SetAll(bool value)
    {
        if (items == null) return;
        foreach (var i in items)
        {
            if (i == null) continue;
            SetState(i, value);
        }
    }

    private static readonly string[] BoolPropNames = { "IsToggled", "isOn", "IsOn", "Value" };

    private bool GetState(Component c)
    {
        if (c == null) return false;
        var t = c.GetType();

        foreach (var name in BoolPropNames)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool))
            {
                try { return (bool)p.GetValue(c); } catch { }
            }
        }

        foreach (var name in BoolPropNames)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool))
            {
                try { return (bool)f.GetValue(c); } catch { }
            }
        }

        return false;
    }

    private void SetState(Component c, bool value)
    {
        if (c == null) return;
        var t = c.GetType();

        var m = t.GetMethod("ForceSetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
        if (m != null) { try { m.Invoke(c, new object[] { value }); return; } catch { } }

        m = t.GetMethod("SetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
        if (m != null) { try { m.Invoke(c, new object[] { value }); return; } catch { } }

        foreach (var name in BoolPropNames)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
            {
                try { p.SetValue(c, value); return; } catch { }
            }
        }

        foreach (var name in BoolPropNames)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool))
            {
                try { f.SetValue(c, value); return; } catch { }
            }
        }

        var valueProp = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (valueProp != null && valueProp.PropertyType == typeof(bool) && valueProp.CanWrite)
        {
            try { valueProp.SetValue(c, value); return; } catch { }
        }
    }

    private void Start()
    {
        if (autoPopulateFromChildren)
        {
            RefreshFromChildren();
        }

        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (master == null)
        {
            Debug.LogWarning("[CheckboxFilterGroup] Master toggle is not assigned; aggregate state cannot be displayed.", this);
        }
        else if (ResolveCompatibleToggle(master) == null)
        {
            Debug.LogWarning($"[CheckboxFilterGroup] Master toggle could not be resolved from '{master.name}'; master-toggle behavior may be unavailable.", this);
        }

        if (items == null || items.Count == 0)
        {
            Debug.LogWarning("[CheckboxFilterGroup] No filter items are configured.", this);
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            Component configuredItem = items[i];
            if (configuredItem == null)
            {
                Debug.LogWarning($"[CheckboxFilterGroup] Filter item {i} is missing.", this);
            }
            else if (ResolveCompatibleToggle(configuredItem) == null)
            {
                Debug.LogWarning($"[CheckboxFilterGroup] Filter item {i} toggle could not be resolved from '{configuredItem.name}'; this filter item may be unavailable.", this);
            }
        }
    }

    private static Component ResolveCompatibleToggle(Component configuredComponent)
    {
        if (configuredComponent == null)
            return null;

        if (HasBoolToggleAPI(configuredComponent))
            return configuredComponent;

        Component compatible = FindCompatibleToggle(configuredComponent.GetComponents<Component>());
        if (compatible != null)
            return compatible;

        compatible = FindCompatibleToggle(configuredComponent.GetComponentsInParent<Component>(true));
        if (compatible != null)
            return compatible;

        return FindCompatibleToggle(configuredComponent.GetComponentsInChildren<Component>(true));
    }

    private static Component FindCompatibleToggle(Component[] components)
    {
        if (components == null)
            return null;

        foreach (Component component in components)
        {
            if (HasBoolToggleAPI(component))
                return component;
        }

        return null;
    }

    private void OnValidate()
    {
        if (autoPopulateFromChildren)
        {
            #if UNITY_EDITOR
            RefreshFromChildren();
            #endif
        }
    }

    [ContextMenu("Refresh From Children")]
    public void RefreshFromChildren()
    {
        var root = scanRoot != null ? scanRoot : transform;
        if (root == null) return;

        var allComps = root.GetComponentsInChildren<Component>(true);
        var byGO = new Dictionary<GameObject, Component>();
        foreach (var c in allComps)
        {
            if (c == null) continue;
            var go = c.gameObject;
            if (go == this.gameObject) continue; 
            if (byGO.ContainsKey(go)) continue;
            if (HasBoolToggleAPI(c))
            {
                byGO[go] = c;
            }
        }

        if (master == null && !string.IsNullOrEmpty(masterNameHint))
        {
            var match = byGO.FirstOrDefault(p => string.Equals(p.Key.name, masterNameHint, System.StringComparison.OrdinalIgnoreCase));
            if (!match.Equals(default(KeyValuePair<GameObject, Component>)))
            {
                master = match.Value;
            }
        }

        var newItems = new List<Component>();
        foreach (var kvp in byGO)
        {
            var comp = kvp.Value;
            if (excludeMasterFromItems && master != null && comp == master) continue;
            newItems.Add(comp);
        }
        items = newItems;
    }

    private static bool HasBoolToggleAPI(Component c)
    {
        if (c == null) return false;
        var t = c.GetType();
        foreach (var name in BoolPropNames)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool)) return true;
        }
        foreach (var name in BoolPropNames)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool)) return true;
        }
        if (t.GetMethod("ForceSetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null) != null) return true;
        if (t.GetMethod("SetToggled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null) != null) return true;
        return false;
    }
}
