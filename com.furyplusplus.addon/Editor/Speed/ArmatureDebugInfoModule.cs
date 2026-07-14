using System;
using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * In preview/non-upload builds VRCFury adds VRCFuryDebugInfo to every merged bone.
     * Large outfits can create thousands of diagnostic-only components which are then
     * carried through pruning, component enumeration and NDMF cloning. Suppress them
     * only for the Armature Link action; runtime bake output is unchanged.
     */
    internal sealed class ArmatureDebugInfoModule : Module<ArmatureDebugInfoModule> {

        internal override string Id => "skipArmatureDebugInfo";
        internal override string DisplayName => "Armature debug-component suppression";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Armature & links";
        internal override string Description =>
            "Skips VRCFuryDebugInfo components on merged bones in preview builds; upload output is unchanged.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ArmatureCompat.EnsureResolved();
            ArmatureDebugInfoPatch.Install(harmony, compat);
        }
    }

    internal static class ArmatureDebugInfoPatch {
        [ThreadStatic] private static bool suppress;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            UploadCompat.EnsureResolved();
            var get = UploadCompat.HookMethod;

            if (!ArmatureCompat.ArmatureLinkAvailable || get == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                ArmatureCompat.ArmatureLinkApply,
                prefix: new HarmonyMethod(typeof(ArmatureDebugInfoPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ArmatureDebugInfoPatch), nameof(End))
            );
            harmony.Patch(
                get,
                prefix: new HarmonyMethod(typeof(ArmatureDebugInfoPatch), nameof(ReportUploading))
            );
        }

        private static void Begin() {
            suppress = ArmatureDebugInfoModule.Instance?.Enabled == true;
        }

        private static Exception End(Exception __exception) {
            suppress = false;
            return __exception;
        }

        private static bool ReportUploading(ref bool __result) {
            if (!suppress) return true;
            // Apply reads this hook exactly once, up front, to decide whether to create
            // debug components. Override only that read: later consumers inside the same
            // phase (component-deletion prevention during pruning, exception reporting)
            // must still see the real upload state.
            suppress = false;
            __result = true;
            return false;
        }
    }
}
