using System;
using System.Linq;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * FuryPlusPlus supersedes QuickFury. Both would stack skip-original prefixes and duplicate
     * indexes on the same VRCFury methods, which is exactly the corruption class that can't be
     * validated. While QuickFury is installed, FuryPlusPlus removes every Harmony patch under
     * QuickFury's ID and warns the user to uninstall the package.
     */
    internal static class Coexistence {
        private const string QuickFuryAssemblyName = "QuickFury.Editor";
        private const string QuickFuryHarmonyId = "com.quickfury.optimizer";
        private const string DialogShownKey = "FuryPlusPlus.QuickFuryDialogShown";

        internal static bool QuickFuryPresent =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == QuickFuryAssemblyName);

        /**
         * QuickFury installs its patches from an [InitializeOnLoad] delayCall that runs in the
         * same flush as Bootstrap.Initialize, in unknown order. Unpatch now (covers a mid-session
         * re-initialize, where QuickFury patched long ago) and again next flush — a delayCall
         * posted during a flush runs in the following one, deterministically after QuickFury's
         * initializer. QuickFury never re-patches until the next domain reload, where this runs
         * again.
         */
        internal static void SuppressQuickFury(Harmony harmony) {
            if (!QuickFuryPresent) return;

            Log.Warn(
                "QuickFury detected. FuryPlusPlus supersedes it — QuickFury's patches are " +
                "disabled for this session. Remove com.quickfury.addon to stop this warning."
            );
            RemoveQuickFuryPatches(harmony);
            EditorApplication.delayCall += () => RemoveQuickFuryPatches(harmony);

            if (!SessionState.GetBool(DialogShownKey, false)) {
                SessionState.SetBool(DialogShownKey, true);
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog(
                    "QuickFury suppressed",
                    "QuickFury is installed alongside FuryPlusPlus.\n\n" +
                    "FuryPlusPlus includes all of QuickFury's optimizations — running both would " +
                    "patch the same VRCFury methods twice, so QuickFury's patches have been " +
                    "disabled for this session.\n\n" +
                    "Remove the com.quickfury.addon package to stop this warning. " +
                    "(Note: QuickFury settings do not carry over.)",
                    "OK"
                );
            }
        }

        private static void RemoveQuickFuryPatches(Harmony harmony) {
            try {
                harmony.UnpatchAll(QuickFuryHarmonyId);
            } catch (Exception e) {
                Log.Warn("Failed to remove QuickFury's patches: " + e.Message);
            }
        }
    }
}
