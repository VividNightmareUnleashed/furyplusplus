using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. This memoized VFController.GetLayers() because the native
     * AnimatorController.layers getter marshals a fresh copy of every layer on each access.
     * VRCFury 1.1364.0 added a native VFController cache (ControllerCache.GetRawLayers) that
     * caches ctrl.layers for the frame, so the remaining benefit — avoiding the VFLayer
     * wrapper re-allocation — measured within run-to-run noise on the pinned version. The
     * patch code is removed; the toggle stays (struck through) pointing at the upstream commit.
     */
    internal sealed class GetLayersMemoModule : Module<GetLayersMemoModule> {

        internal override string Id => "getLayersMemo";
        internal override string DisplayName => "Controller layer-list cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's native VFController layer cache (1.1364.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1364.0",
            "VRCFury caches ctrl.layers natively in VFController (GetRawLayers).",
            "https://github.com/VRCFury/VRCFury/commit/40b63b38eb1b0dc0152e82c032e3ad50f375656d");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
