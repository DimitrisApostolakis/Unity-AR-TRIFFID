using UnityEditor;

[CustomEditor(typeof(JsonSpawner)), CanEditMultipleObjects]
public sealed class JsonSpawnerEditor : TriffidGroupedEditor
{
    protected override string Description => "Owns feature spawning, local saves and surface placement. Server delivery is configured on GeoJsonSyncCoordinator.";
}

[CustomEditor(typeof(GeoJsonSyncCoordinator)), CanEditMultipleObjects]
public sealed class GeoJsonSyncCoordinatorEditor : TriffidGroupedEditor
{
    protected override string Description => "Delivers saved edits and applies server snapshots. References and startup settings come first; retry, polling and observer settings are grouped below.";
}

[CustomEditor(typeof(GeoJsonApiManager)), CanEditMultipleObjects]
public sealed class GeoJsonApiManagerEditor : TriffidGroupedEditor
{
    protected override string Description => "HTTP transport and endpoint environment. Scheduling, retries and remote-refresh acknowledgement belong to GeoJsonSyncCoordinator.";
}

[CustomEditor(typeof(MapColliderSync)), CanEditMultipleObjects]
public sealed class MapColliderSyncEditor : TriffidGroupedEditor
{
    protected override string Description => "Aligns the collision object with the map and splat renderer. The COLMAP aligner can call this after applying the renderer rotation.";
    protected override void DrawActions()
    {
        if (targets.Length == 1)
            ArPipelineInspectorGui.DrawActionButtons("Sync collider to map", ((MapColliderSync)target).SyncToMap);
    }
}

[CustomEditor(typeof(XRControllerLogger)), CanEditMultipleObjects]
public sealed class XRControllerLoggerEditor : TriffidGroupedEditor
{
    protected override string Description => "Controller/hand interaction, placement and drawing. Existing input bindings and menu references remain on this component.";
}

[CustomEditor(typeof(PointInfoPanel)), CanEditMultipleObjects]
public sealed class PointInfoPanelEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(MenuButtonController)), CanEditMultipleObjects]
public sealed class MenuButtonControllerEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(MenuStateRestorer)), CanEditMultipleObjects]
public sealed class MenuStateRestorerEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(CheckboxFilterGroup)), CanEditMultipleObjects]
public sealed class CheckboxFilterGroupEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(CheckboxFilterHook)), CanEditMultipleObjects]
public sealed class CheckboxFilterHookEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(FloatingIcon)), CanEditMultipleObjects]
public sealed class FloatingIconEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(PointData)), CanEditMultipleObjects]
public sealed class PointDataEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(FreeLocomotion)), CanEditMultipleObjects]
public sealed class FreeLocomotionEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(XRControllerInputBridge)), CanEditMultipleObjects]
public sealed class XRControllerInputBridgeEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(MapReset)), CanEditMultipleObjects]
public sealed class MapResetEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(ScaleChanger)), CanEditMultipleObjects]
public sealed class ScaleChangerEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(LineWidthController)), CanEditMultipleObjects]
public sealed class LineWidthControllerEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(CameraBackgroundToggle)), CanEditMultipleObjects]
public sealed class CameraBackgroundToggleEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(DeleteConfirmButton)), CanEditMultipleObjects]
public sealed class DeleteConfirmButtonEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(TransformSliderControl)), CanEditMultipleObjects]
public sealed class TransformSliderControlEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(RainbowCircleImage)), CanEditMultipleObjects]
public sealed class RainbowCircleImageEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(PulseEffect)), CanEditMultipleObjects]
public sealed class PulseEffectEditor : TriffidGroupedEditor { }
[CustomEditor(typeof(MarkerInteractionRelay)), CanEditMultipleObjects]
public sealed class MarkerInteractionRelayEditor : TriffidGroupedEditor { }
