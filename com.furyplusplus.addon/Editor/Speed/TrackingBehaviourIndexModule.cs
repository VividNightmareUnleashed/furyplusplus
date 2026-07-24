using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. TrackingConflictResolverService materializes every layer's behaviour set to
     * find contributors, then asks every layer to rewrite its tracking controls — and VFLayer
     * used to rebuild the recursive immutable behaviour-container graph out of the live
     * AnimatorStateMachine for both passes. This module held that graph for the duration of
     * Apply and skipped the second traversal for layers the first proved irrelevant.
     *
     * The detached controller model (1.1382.0) removed the construction entirely: behaviour
     * containers are stored fields on resident VFStateMachine/VFState objects, so both passes
     * now walk in-memory state with nothing to cache between them. The patch code is removed
     * and the toggle stays (struck through). The phase measures ~3ms at this pin.
     */
    internal sealed class TrackingBehaviourIndexModule : Module<TrackingBehaviourIndexModule> {

        internal override string Id => "trackingBehaviourIndex";
        internal override string DisplayName => "Tracking behaviour index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's detached controller model (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "Behaviour containers are now stored on resident VFStateMachine/VFState objects " +
            "rather than rebuilt from the native state machine on every access.",
            "https://github.com/VRCFury/VRCFury/commit/21674b90da9a4d7ed1cec1f4b443d17ece7be916");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
