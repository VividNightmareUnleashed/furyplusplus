using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. Several late controller services invoke VFLayer.RewriteBehaviours on every
     * layer even though only a small subset holds the requested behaviour type, and VFLayer
     * used to rebuild a recursive immutable VFBehaviourContainer graph out of the live
     * AnimatorStateMachine on each access. This module proved layers empty with a cheap raw
     * array scan first, so that graph was never built for them.
     *
     * The detached controller model (1.1382.0) removed the construction entirely: state
     * machines and states are resident VFStateMachine/VFState objects that each hold their
     * VFBehaviourContainer as a stored field, and allBehaviourContainers is now a lazy LINQ
     * walk over them. There is no graph build left to skip, so the patch code is removed and
     * the toggle stays (struck through). The services in question total well under 15ms of a
     * bake at this pin.
     */
    internal sealed class BehaviourContainerFilterModule : Module<BehaviourContainerFilterModule> {

        internal override string Id => "behaviourContainerFilter";
        internal override string DisplayName => "Behaviour container filter";
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
