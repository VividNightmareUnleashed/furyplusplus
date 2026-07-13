using UnityEditor;

namespace FuryPlusPlus {
    internal static class MenuItems {
        internal const string Root = "Tools/FuryPlusPlus/";

        [MenuItem(Root + "Settings…", priority = 0)]
        private static void OpenSettings() {
            SettingsWindow.Open();
        }

        [MenuItem(Root + "Welcome", priority = 1)]
        private static void OpenWelcome() {
            SettingsWindow.Open();
        }

        [MenuItem(Root + "Log last profile report", priority = 20)]
        private static void LogLastReport() {
            var report = FuryPlusPlusProfilerApi.LastReport;
            if (string.IsNullOrEmpty(report)) {
                report = SessionState.GetString("FuryPlusPlus.LastProfile", "");
            }
            if (string.IsNullOrEmpty(report)) {
                Log.Info("No profile report captured yet — run a VRCFury bake first.");
            } else {
                UnityEngine.Debug.Log(report);
            }
        }

        [MenuItem(Root + "Disable all optimizations", priority = 40)]
        private static void DisableAll() {
            Settings.DisableAllModules();
            Log.Info("All modules disabled (master switch unchanged).");
        }
    }
}
