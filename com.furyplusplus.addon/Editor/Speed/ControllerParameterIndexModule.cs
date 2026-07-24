using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. VFController.GetParam used to be Array.Find(ctrl.parameters, …) against a
     * live AnimatorController, so every lookup marshalled a fresh copy of the entire
     * parameter array across the native boundary — and VRCFury looks a parameter up before
     * creating each one. This module kept an exact name index to avoid that. As of the
     * detached controller model (1.1382.0) VFController owns a plain managed
     * List&lt;AnimatorControllerParameter&gt;; GetParam is a FirstOrDefault over it and the
     * native round-trip is gone entirely.
     *
     * What remains upstream is a managed O(n) scan plus an O(n) list copy per created
     * parameter — still quadratic in parameter count, but over references rather than
     * marshalled structs, which puts it far below the noise floor of a bake. Not worth an
     * index, so the patch code is removed and the toggle stays (struck through).
     */
    internal sealed class ControllerParameterIndexModule : Module<ControllerParameterIndexModule> {

        internal override string Id => "controllerParameterIndex";
        internal override string DisplayName => "Controller parameter index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's detached controller model (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "VFController now holds its parameters as a managed list, so looking one up no " +
            "longer marshals the whole array out of a native AnimatorController.",
            "https://github.com/VRCFury/VRCFury/commit/21674b90da9a4d7ed1cec1f4b443d17ece7be916");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
