namespace FuryPlusPlus {
    internal static class MenuItems {
        internal const string Root = "Tools/FuryPlusPlus/";

        [UnityEditor.MenuItem(Root + "Settings…", priority = 0)]
        private static void OpenSettings() {
            SettingsWindow.Open();
        }

        [UnityEditor.MenuItem(Root + "Log last profile report", priority = 20)]
        private static void LogLastReport() {
            FuryPlusPlusProfilerApi.LogLastReport();
        }

        [UnityEditor.MenuItem(Root + "Disable all optimizations", priority = 40)]
        private static void DisableAll() {
            Settings.DisableAllModules();
            Log.Info("All modules disabled (master switch unchanged).");
        }
    }
}
