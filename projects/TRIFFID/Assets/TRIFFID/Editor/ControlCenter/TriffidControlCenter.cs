using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// One window for everything that is normally tuned. It is a view, not a second
/// copy: scene settings are edited in place on their own components through
/// SerializedObject (with Undo and prefab overrides as usual), and visual
/// settings are edited on the TriffidVisualSettings asset. Nothing is moved,
/// renamed or duplicated, so runtime behaviour and saved scene values are
/// untouched. A property that no longer exists is simply not drawn.
/// </summary>
public sealed class TriffidControlCenter : EditorWindow
{
    private sealed class Block
    {
        public string heading;
        public string help;
        public Type component;          // null = the visual settings asset
        public string[] properties;

        public Block(string heading, Type component, string help, params string[] properties)
        {
            this.heading = heading;
            this.component = component;
            this.help = help;
            this.properties = properties;
        }
    }

    private sealed class Tab
    {
        public string title;
        public Block[] blocks;

        public Tab(string title, params Block[] blocks)
        {
            this.title = title;
            this.blocks = blocks;
        }
    }

    private static readonly Tab[] Tabs =
    {
        new Tab("Classes",
            new Block("Class groups and colours", null,
                "Each class belongs to one colour group. The group colour is used for its footprint ring, pulse, info panel title and menu caption.",
                "classes", "activeHazard", "obstacle", "life", "asset", "neutral"),
            new Block("Map prefab per class", typeof(JsonSpawner),
                "Which prefab is spawned for each GeoJSON class name.",
                "prefabEntries"),
            new Block("Prefabs offered in the Points menu", typeof(XRControllerLogger),
                "Order matches the Points menu buttons.",
                "availablePointPrefabs"),
            new Block("Info panel icon per class", typeof(PointInfoPanel), null,
                "spritesList", "defaultSprite", "ugvSprite", "lineSprite")),

        new Tab("Markers",
            new Block("Footprint", typeof(JsonSpawner), null,
                "showMarkerFootprints"),
            new Block("Footprint size", null,
                "All sizes are relative, so footprints follow the icon and the map scale.",
                "footprintRingRadius", "footprintRingWidth", "footprintFocusRingWidth",
                "footprintDotRadius", "footprintStemWidth", "footprintFocusScale"),
            new Block("Icon motion", null, null,
                "overrideIconBob", "iconBobAmplitude", "iconBobFrequency"),
            new Block("Selection pulse", null, null,
                "pulseGroundWaves", "pulseWaveSeconds", "pulseWaveReach", "pulseHaloAlpha",
                "overrideHalo", "haloSeconds", "haloMaxScale")),

        new Tab("Lines & Polygons",
            new Block("Mission Planner routes", null,
                "A drawn line is saved as a Responders route (class responder_path) or a UGV route (class ugv_path). Loaded routes of either class use the same colours.",
                "responderPathColor", "ugvPathColor"),
            new Block("Look", null, null,
                "polygonFills", "polygonFillAlpha", "lineCornerVertices"),
            new Block("Selected line or polygon", null, null,
                "lineSelectionGlow", "lineGlowWidth", "lineGlowAlpha", "lineGlowSeconds", "polygonSelectedFillBoost"),
            new Block("Lines loaded from GeoJSON", typeof(JsonSpawner), null,
                "lineThickness", "lineMaterial", "colorPalette", "nodePrefab"),
            new Block("Lines drawn with the controller", typeof(XRControllerLogger), null,
                "lineWidth", "lineMaterial", "lineNodePrefab", "colorRed", "colorYellow", "colorBlue", "baseVerticalOffset"),
            new Block("Line width toggle", typeof(LineWidthController), null,
                "normalLineWidth", "wideLineWidth", "animateTransition", "widthLerpSpeed")),

        new Tab("Menus & Panel",
            new Block("Styling switches", null, null,
                "menuCellLayout", "colorMenuCaptions", "infoPanelStyling", "nameSpeechHints"),
            new Block("Filter and spawn menu cells", null, null,
                "cellIconSize", "cellIconPosition", "cellCaptionPosition", "cellCheckboxPosition",
                "cellCheckboxScale", "cellOffIconAlpha", "cellOffCaptionAlpha"),
            new Block("Menu placement", typeof(XRControllerLogger), null,
                "spawnDistanceFromHead", "spawnHeightOffset", "keepMenuVertical", "faceEyesWhenLookingUpDown",
                "additionalRotationOffset", "keepSecondaryMenusVertical", "flipSecondaryMenusFacing",
                "secondaryMenusRotationOffset", "slotOffsetRight", "slotOffsetLeft", "slotOffsetTop"),
            new Block("Mode buttons", typeof(MenuButtonController), null,
                "useLockClickedToggle", "keepMapColliderDisabledWhileWalk",
                "useBlurLikeDisabledVisual", "disabledAlpha", "disabledSaturation"),
            new Block("Info panel", typeof(PointInfoPanel), null,
                "fadeDuration")),

        new Tab("Map & Data",
            new Block("Files", typeof(JsonSpawner), null,
                "geoJsonPath", "transformJsonPath", "saveFilePath", "saveInsideProjectFolder", "mapTransform"),
            new Block("Saving", typeof(JsonSpawner), null,
                "enableAutoSaveTimer", "autoSaveIntervalSeconds", "logSaveStageTimings"),
            new Block("Surface placement", typeof(JsonSpawner),
                "Distances are in physical metres.",
                "enableSurfaceSnapFallback", "surfaceSnapTargetObject", "surfaceSnapMask",
                "surfaceSnapRayStartOffset", "surfaceSnapRayDistance", "surfaceSnapOffset", "surfaceSnapLift"),
            new Block("Annotation colliders", typeof(JsonSpawner), null,
                "startWithCollidersEnabled"),
            new Block("Collision mesh", typeof(MeshPathJsonLoader), null,
                "meshPath", "meshLoadMode", "loadOnStart", "loadOnEnable", "convexCollider")),

        new Tab("Movement",
            new Block("Walk mode", typeof(FreeLocomotion), null,
                "moveSpeed", "verticalSpeed", "rotationDegrees", "snapCooldown", "rotateAroundCamera")),

        new Tab("Sync",
            new Block("Server", typeof(GeoJsonApiManager), null,
                "useLocalhost", "requestTimeoutSeconds", "verboseLogs",
                "enableStatusPatchOnLocalUpdate"),
            new Block("Polling", typeof(GeoJsonSyncCoordinator), null,
                "loadFromServerOnEnable", "enableRemotePolling", "remotePollingIntervalSeconds",
                "enableLatestMqttPolling", "latestMqttPollingIntervalSeconds"),
            new Block("Retries and timing", typeof(GeoJsonSyncCoordinator), null,
                "maxRetries", "retryBaseDelaySeconds", "retryMaxDelaySeconds",
                "updateDebounceSeconds", "localChangeGraceSeconds", "apiCallbackTimeoutSeconds", "verboseLogs")),
    };

    private const string TabKey = "TRIFFID.ControlCenter.Tab";

    private readonly Dictionary<Type, SerializedObject> sceneObjects = new Dictionary<Type, SerializedObject>();
    private SerializedObject settingsObject;
    private Vector2 scroll;
    private string search = string.Empty;
    private int tabIndex;

    [MenuItem("Tools/TRIFFID Control Center")]
    public static void Open()
    {
        TriffidControlCenter window = GetWindow<TriffidControlCenter>();
        window.titleContent = new GUIContent("TRIFFID Control Center");
        window.minSize = new Vector2(420f, 360f);
        window.Show();
    }

    private void OnEnable()
    {
        tabIndex = Mathf.Clamp(SessionState.GetInt(TabKey, 0), 0, Tabs.Length - 1);
    }

    // Scene objects come and go with scene loads, Play mode and undo.
    private void OnHierarchyChange() { ClearCache(); Repaint(); }
    private void OnFocus() { ClearCache(); }
    private void OnInspectorUpdate() { Repaint(); }

    private void ClearCache()
    {
        sceneObjects.Clear();
        settingsObject = null;
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("Settings asset", EditorStyles.toolbarButton, GUILayout.Width(100f)))
            {
                SerializedObject settings = GetSettings();
                if (settings != null) EditorGUIUtility.PingObject(settings.targetObject);
            }
        }

        bool searching = !string.IsNullOrWhiteSpace(search);
        if (!searching)
        {
            string[] titles = new string[Tabs.Length];
            for (int i = 0; i < Tabs.Length; i++) titles[i] = Tabs[i].title;

            int selected = GUILayout.SelectionGrid(tabIndex, titles, 4);
            if (selected != tabIndex)
            {
                tabIndex = selected;
                SessionState.SetInt(TabKey, tabIndex);
                scroll = Vector2.zero;
            }
        }

        if (EditorApplication.isPlaying)
            EditorGUILayout.HelpBox("Play mode: visual settings are read when a marker, line or menu is set up, so some changes show only on the next spawn or the next Play. Scene values changed here are reverted when Play stops, as usual.", MessageType.Info);

        using (var view = new EditorGUILayout.ScrollViewScope(scroll))
        {
            scroll = view.scrollPosition;
            if (searching)
            {
                // Search looks through every tab.
                foreach (Tab tab in Tabs)
                    foreach (Block block in tab.blocks)
                        DrawBlock(block, tab.title, search.Trim());
            }
            else
            {
                foreach (Block block in Tabs[tabIndex].blocks)
                    DrawBlock(block, null, null);
            }
        }
    }

    private void DrawBlock(Block block, string tabTitle, string filter)
    {
        SerializedObject owner = block.component == null ? GetSettings() : GetSceneObject(block.component);

        if (owner == null)
        {
            if (filter != null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(block.heading, EditorStyles.boldLabel);
            if (block.component == null) DrawCreateSettings();
            else EditorGUILayout.HelpBox("No " + block.component.Name + " in the open scene.", MessageType.None);
            return;
        }

        owner.Update();

        var visible = new List<SerializedProperty>();
        foreach (string path in block.properties)
        {
            SerializedProperty property = owner.FindProperty(path);
            if (property == null) continue;
            if (filter != null && !Matches(block.heading, filter) && !Matches(property.displayName, filter) &&
                !Matches(property.name, filter) && !Matches(property.tooltip, filter)) continue;
            visible.Add(property);
        }
        if (visible.Count == 0) return;

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            string heading = tabTitle == null ? block.heading : tabTitle + "  /  " + block.heading;
            EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);

            // Where the values actually live, one click away.
            string ownerName = block.component == null ? "Settings asset" : block.component.Name;
            if (GUILayout.Button(new GUIContent(ownerName, "Select the object that owns these settings to see its full Inspector."),
                    EditorStyles.miniButton, GUILayout.Width(170f)))
            {
                Selection.objects = owner.targetObjects;
                EditorGUIUtility.PingObject(owner.targetObject);
            }
        }

        if (!string.IsNullOrEmpty(block.help) && filter == null)
            EditorGUILayout.LabelField(block.help, EditorStyles.wordWrappedMiniLabel);

        using (new EditorGUI.IndentLevelScope())
        {
            foreach (SerializedProperty property in visible)
                EditorGUILayout.PropertyField(property, true);
        }

        owner.ApplyModifiedProperties();
    }

    private static bool Matches(string value, string filter)
    {
        return !string.IsNullOrEmpty(value) && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private SerializedObject GetSceneObject(Type type)
    {
        SerializedObject cached;
        if (sceneObjects.TryGetValue(type, out cached) && cached != null && cached.targetObject != null)
            return cached;

        Object[] found = FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
        cached = found != null && found.Length > 0 ? new SerializedObject(found) : null;
        sceneObjects[type] = cached;
        return cached;
    }

    private SerializedObject GetSettings()
    {
        if (settingsObject != null && settingsObject.targetObject != null)
            return settingsObject;

        TriffidVisualSettings asset = Resources.Load<TriffidVisualSettings>(TriffidVisualSettings.ResourceName);
        settingsObject = asset != null ? new SerializedObject(asset) : null;
        return settingsObject;
    }

    private void DrawCreateSettings()
    {
        EditorGUILayout.HelpBox("No TriffidVisualSettings asset was found in a Resources folder. The app runs on built-in defaults until one exists.", MessageType.Warning);
        if (!GUILayout.Button("Create settings asset")) return;

        const string folder = "Assets/TRIFFID/Resources";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets/TRIFFID", "Resources");

        TriffidVisualSettings asset = CreateInstance<TriffidVisualSettings>();
        AssetDatabase.CreateAsset(asset, folder + "/" + TriffidVisualSettings.ResourceName + ".asset");
        AssetDatabase.SaveAssets();
        ClearCache();
    }
}

/// <summary>
/// The settings asset keeps Unity's normal Inspector and adds the way in to the
/// Control Center, where the same values sit beside the related scene settings.
/// </summary>
[CustomEditor(typeof(TriffidVisualSettings))]
public sealed class TriffidVisualSettingsEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (GUILayout.Button("Open TRIFFID Control Center", GUILayout.Height(26f)))
            TriffidControlCenter.Open();

        EditorGUILayout.Space();
        DrawDefaultInspector();
    }
}
