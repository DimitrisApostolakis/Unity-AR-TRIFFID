using System;

public enum MarkerFocusReason
{
    Hover,
    Select
}

public interface IMarkerFocusable
{
    PointData MarkerData { get; }
    void FocusMarker(MarkerFocusReason reason);
}

public static class MarkerEventManager
{
    public static event Action<PointData> MarkerHovered;
    public static event Action<PointData> MarkerSelected;
    public static event Action<PointData> MarkerMoved;

    public static void RaiseMarkerHovered(PointData data)
    {
        if (data == null)
            return;

        MarkerHovered?.Invoke(data);
    }

    public static void RaiseMarkerSelected(PointData data)
    {
        if (data == null)
            return;

        MarkerSelected?.Invoke(data);
    }

    public static void RaiseMarkerMoved(PointData data)
    {
        if (data == null)
            return;

        MarkerMoved?.Invoke(data);
    }
}
