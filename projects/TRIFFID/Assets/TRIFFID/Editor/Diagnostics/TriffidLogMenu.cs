using UnityEditor;

// Editor toggle for TriffidLog verbose output, remembered per user via EditorPrefs.
[InitializeOnLoad]
internal static class TriffidLogMenu
{
    private const string MenuPath = "Tools/TRIFFID/Verbose Logging";
    private const string PrefKey = "TRIFFID.VerboseLogging";

    static TriffidLogMenu()
    {
        TriffidLog.VerboseEnabled = EditorPrefs.GetBool(PrefKey, TriffidLog.VerboseEnabled);
    }

    [MenuItem(MenuPath, priority = 200)]
    private static void Toggle()
    {
        TriffidLog.VerboseEnabled = !TriffidLog.VerboseEnabled;
        EditorPrefs.SetBool(PrefKey, TriffidLog.VerboseEnabled);
        TriffidLog.Info("Logging", "Verbose logging " + (TriffidLog.VerboseEnabled ? "enabled." : "disabled."));
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, TriffidLog.VerboseEnabled);
        return true;
    }
}
