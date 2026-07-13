using System;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    [InitializeOnLoad]
    internal static class Bootstrap {
        internal const string HarmonyId = "com.furyplusplus.optimizer";

        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        internal static VrcfuryCompat Compat { get; private set; }

        /** Human-readable reason the addon is (fully) inactive, for the settings/welcome UI. */
        internal static string DisabledReason { get; private set; }

        static Bootstrap() {
            AssemblyReloadEvents.beforeAssemblyReload += Unpatch;
            EditorApplication.delayCall += Initialize;
        }

        /**
         * Also callable from the settings UI after the master switch is turned back on
         * mid-session; Unpatch-first keeps it idempotent.
         */
        internal static void Initialize() {
            Unpatch();
            Compat = null;
            DisabledReason = null;

            Coexistence.SuppressQuickFury(Harmony);
            if (!Settings.MasterEnabled) {
                DisabledReason = "master switch off";
                Log.Info("Disabled by master switch.");
                return;
            }
            if (!VrcfuryCompat.TryCreate(out var compat, out var error)) {
                DisabledReason = error;
                Log.Warn("Disabled: " + error);
                return;
            }

            Compat = compat;
            try {
                // Before modules install: their Install() calls RegisterBefore/After.
                BuildPhaseHooks.Install(Harmony, compat);
            } catch (Exception e) {
                Log.Warn("Build phase hooks unavailable (dependent modules will fail closed): " + e.Message);
            }
            ModuleRegistry.InstallAll(Harmony, compat);

            // First install (or major update): open the FuryPlusPlus window once.
            const string welcomeVersion = "0.1.0";
            if (Settings.MasterEnabled
                && EditorPrefs.GetString(Settings.WelcomeShownVersionKey, "") != welcomeVersion) {
                EditorPrefs.SetString(Settings.WelcomeShownVersionKey, welcomeVersion);
                EditorApplication.delayCall += SettingsWindow.Open;
            }
        }

        private static void Unpatch() {
            try {
                Harmony.UnpatchAll(HarmonyId);
            } catch (Exception e) {
                Log.Warn("Failed to remove old patches: " + e.Message);
            }
        }
    }
}
