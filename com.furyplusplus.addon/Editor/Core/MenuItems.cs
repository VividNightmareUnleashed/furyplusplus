using UnityEditor;

namespace FuryPlusPlus {
    internal static class MenuItems {
        internal const string Root = "Tools/FuryPlusPlus/";

        [MenuItem(Root + "Settings…", priority = 0)]
        private static void OpenSettings() {
            SettingsWindow.Open();
        }

        [MenuItem(Root + "Disable all optimizations", priority = 40)]
        private static void DisableAll() {
            Settings.DisableAllModules();
            Log.Info("All modules disabled (master switch unchanged).");
        }
    }
}
