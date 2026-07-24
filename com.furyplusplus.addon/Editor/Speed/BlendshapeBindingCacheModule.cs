using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. BlendshapeOptimizerBuilder used to call GetBindings from inside
     * CollectAnimatedBlendshapesForMesh, so every clip and float curve in every controller was
     * re-enumerated once per skinned mesh; this cached that result per (owner, controller) for
     * the duration of Apply. VRCFury 1.1372.0 hoisted the scan out of the loop itself — Apply
     * now builds the blendshape curve list once and hands CollectAnimatedBlendshapesForMesh a
     * Lazy over it. There is no per-mesh rescan left to cache, so the patch code is removed and
     * the toggle stays (struck through) pointing at the upstream commit.
     */
    internal sealed class BlendshapeBindingCacheModule : Module<BlendshapeBindingCacheModule> {

        internal override string Id => "blendshapeBindingCache";
        internal override string DisplayName => "Blendshape binding cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's native hoist of the blendshape curve scan (1.1372.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1372.0",
            "BlendshapeOptimizer now collects the blendshape curves once per Apply instead of once per skinned mesh.",
            "https://github.com/VRCFury/VRCFury/commit/fce41fe6123787a195283e5e0ee4410ec6fca0b0");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
