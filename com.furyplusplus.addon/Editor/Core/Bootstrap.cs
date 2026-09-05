using System;
using System.IO;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    [InitializeOnLoad]
    internal static class Bootstrap {
        internal const string HarmonyId = "com.furyplusplus.optimizer";

        private const string UnsupportedDialogShownKey = "FuryPlusPlus.UnsupportedDialogShown";

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

            // Before the compat gate: the first-open welcome must appear even when VRCFury
            // is missing or unsupported — that window's banner is what explains the problem.
            CompatibilityApprovals.Initialize();
            MaybeShowWelcome();

            if (!VrcfuryCompat.TryCreate(out var compat, out var error)) {
                DisabledReason = error;
                Log.Warn("Disabled: " + error);
                // VRCFury present but its profiling anchors unresolvable = an untested
                // release; VRCFury absent entirely stays a console warning (nothing to do).
                var loadedVersion = VrcfuryCompat.LoadedPackageVersion();
                if (loadedVersion != null) WarnUnsupportedVersion(loadedVersion);
                return;
            }

            Compat = compat;
            if (compat.IsApproved) {
                try {
                    // Before modules install: their Install() calls RegisterBefore/After.
                    BuildPhaseHooks.Install(Harmony, compat);
                } catch (Exception e) {
                    Log.Warn("Build phase hooks unavailable (dependent modules will fail closed): " + e.Message);
                }
            }
            ModuleRegistry.InstallAll(Harmony, compat);

            if (!compat.IsApproved) WarnUnsupportedVersion(compat.PackageVersion);
        }

        /**
         * EditorPrefs is machine-wide, so the welcome flag lives in the project's
         * UserSettings folder — every fresh project greets exactly once.
         */
        private static string WelcomeMarkerPath() {
            var project = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(project, "UserSettings", "FuryPlusPlusWelcome.txt");
        }

        private static void MaybeShowWelcome() {
            const string welcomeVersion = "0.1.0";
            try {
                var path = WelcomeMarkerPath();
                if (File.Exists(path) && File.ReadAllText(path).Trim() == welcomeVersion) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, welcomeVersion);
            } catch (Exception e) {
                // Without a persisted marker the window would pop on every domain reload.
                Log.Warn("Could not save the welcome marker, skipping the welcome window: " + e.Message);
                return;
            }
            EditorApplication.delayCall += SettingsWindow.Open;
        }

        /**
         * An unapproved combination means every version-gated module failed closed — the addon is
         * effectively off. A console line is too easy to miss for that, so this is a modal
         * dialog, once per editor session (same policy as the QuickFury coexistence dialog).
         */
        private static void WarnUnsupportedVersion(string vrcfuryVersion) {
            Log.Warn(
                $"FuryPlusPlus {PackageIdentity.Version} with VRCFury {vrcfuryVersion} " +
                "has no current approval; optimizations are disabled."
            );
            if (SessionState.GetBool(UnsupportedDialogShownKey, false)) return;
            SessionState.SetBool(UnsupportedDialogShownKey, true);
            EditorApplication.delayCall += () => {
                if (EditorUtility.DisplayDialog(
                        "FuryPlusPlus — optimizations disabled",
                        $"FuryPlusPlus {PackageIdentity.Version} with VRCFury {vrcfuryVersion} " +
                        "has no current approval in this Editor session.\n\n" +
                        "Avatars bake with stock VRCFury; profiling and editor visuals remain eligible. " +
                        "Check approvals in FuryPlusPlus settings. If an approval is downloaded, " +
                        "restart Unity or reload scripts to apply it.",
                        "Open FuryPlusPlus", "Close")) {
                    SettingsWindow.Open();
                }
            };
        }

        private static void Unpatch() {
            try {
                Harmony.UnpatchAll(HarmonyId);
            } catch (Exception e) {
                Log.Warn("Failed to remove old patches: " + e.Message);
            }
            // Shared patch scopes latch their installed state; a mid-session re-init
            // (master switch off→on) must make them re-patch, not skip.
            BakeChainAnchor.NotifyUnpatched();
            CompressorScope.NotifyUnpatched();
        }
    }
}
