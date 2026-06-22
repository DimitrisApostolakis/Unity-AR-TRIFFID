using UnityEngine;
using System;

public class PointData : MonoBehaviour
{
    public string pointClass = "-";
    public string pointID    = "-";
    public string category   = "-";
    public string source     = "-";
    public string behavior   = "-";
    public string lineColorHex = "";
    public double latitude   = 0.0;
    public double longitude  = 0.0;
    public double altitude   = 0.0; 
    public float  confidence = 0f;

    public Action OnDataChanged;

    public void OnPointSelected()
    {
        MarkerEventManager.RaiseMarkerSelected(this);
    }

    public void NotifyDataChanged()
    {
        OnDataChanged?.Invoke();
    }
}