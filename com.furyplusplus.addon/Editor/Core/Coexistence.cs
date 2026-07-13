using System;
using System.Linq;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * FuryPlusPlus supersedes QuickFury and refuses to initialize entirely while it is
     * installed: both would stack skip-original prefixes and duplicate indexes on the same
     * VRCFury methods, which is exactly the corruption class that can't be validated.
     * We never touch QuickFury's Harmony state — the user must remove the package.
     */
    internal static class Coexistence {
        private const string QuickFuryAssemblyName = "QuickFury.Editor";
        private const string DialogShownKey = "FuryPlusPlus.QuickFuryDialogShown";

        internal static bool QuickFuryPresent =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == QuickFuryAssemblyName);

        /** Returns true when FuryPlusPlus must refuse to run (and has told the user why). */
        internal static bool RefuseIfQuickFury() {
            if (!QuickFuryPresent) return false;

            Log.Error(
                "QuickFury detected. FuryPlusPlus supersedes it — remove com.quickfury.addon. " +
                "All FuryPlusPlus functionality is disabled until then."
            );
            if (!SessionState.GetBool(DialogShownKey, false)) {
                SessionState.SetBool(DialogShownKey, true);
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog(
                    "FuryPlusPlus disabled",
                    "QuickFury is installed alongside FuryPlusPlus.\n\n" +
                    "FuryPlusPlus includes all of QuickFury's optimizations — running both would " +
                    "patch the same VRCFury methods twice.\n\n" +
                    "Remove the com.quickfury.addon package to enable FuryPlusPlus. " +
                    "(Note: QuickFury settings do not carry over.)",
                    "OK"
                );
            }
            return true;
        }
    }
}
