using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. This kept a short-lived state-machine-to-index table during
     * LayerToTreeService.Apply so VFLayer.GetLayerId / Exists were O(1) instead of a linear
     * AnimatorController.layers scan. VRCFury 1.1364.0 gave VFController a native
     * ControllerCache with exactly that: GetLayerId(stateMachine) backed by a cached
     * stateMachine→index dictionary. Benchmarked on the pinned version, F++'s index no longer
     * measurably beats the native one (within run-to-run noise), so its patch code is removed;
     * the toggle stays (struck through) pointing at the upstream commit.
     */
    internal sealed class LayerToTreeLayerIndexModule : Module<LayerToTreeLayerIndexModule> {

        internal override string Id => "layerToTreeLayerIndex";
        internal override string DisplayName => "Layer-to-tree layer index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's native VFController layer-id cache (1.1364.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1364.0",
            "VRCFury caches layer ids natively in VFController (GetLayerId / cache.layerIds).",
            "https://github.com/VRCFury/VRCFury/commit/40b63b38eb1b0dc0152e82c032e3ad50f375656d");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
