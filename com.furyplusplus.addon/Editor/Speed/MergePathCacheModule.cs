using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. ValidateBindingsService.IsValid used to take an EditorCurveBinding and
     * resolve it from scratch on every call — a transform-path Find plus a GetComponent —
     * and FullControllerBuilder's CreateNearestMatchPathRewriter probed it once per ancestor
     * level per distinct binding per clip. This module memoized those answers for the
     * duration of the merge phase.
     *
     * Both halves of that are gone at 1.1382.0. CreateNearestMatchPathRewriter no longer
     * exists, and IsValid now takes a VFBinding whose target is an already-resolved object:
     * it reads binding.target and does a single IsValidResolvedTarget check. The ancestor
     * walk still exists, but it moved into binding *resolution*, which happens once when a
     * controller is loaded rather than on every validity probe. There is no repeated path
     * search left to memoize, so the patch code is removed and the toggle stays (struck
     * through).
     */
    internal sealed class MergePathCacheModule : Module<MergePathCacheModule> {

        internal override string Id => "mergePathCache";
        internal override string DisplayName => "Full Controller merge path cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Paths & rewriting";
        internal override string Description =>
            "Superseded by VRCFury's resolved-object bindings (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "Bindings carry an already-resolved target object, so validating one no longer " +
            "searches the hierarchy by path.",
            "https://github.com/VRCFury/VRCFury/commit/21674b90da9a4d7ed1cec1f4b443d17ece7be916");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
