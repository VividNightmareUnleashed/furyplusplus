using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. LayerToTreeService.OptimizeLayer used to enumerate EVERY layer's binding
     * set for EACH candidate layer — O(L²·B). This module replaced Apply and handed
     * OptimizeLayer an inverted binding→layers index so only layers actually sharing a
     * binding were visited. VRCFury 1.1372.0 builds both maps itself: Apply now computes
     * bindingsByLayer once and inverts it into layersByBinding, then threads both into
     * OptimizeLayer. That is the same index, so the patch code is removed and the toggle
     * stays (struck through).
     *
     * The phase went from the dominant cost of a layer-heavy bake to ~52ms, under 2% of
     * the total. OffSideEliminationModule now reads VRCFury's maps off the OptimizeLayer
     * call rather than depending on this module to publish them.
     */
    internal sealed class LayerToTreeBindingIndexModule : Module<LayerToTreeBindingIndexModule> {

        internal override string Id => "layerToTreeBindingIndex";
        internal override string DisplayName => "Layer-to-tree binding index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded by VRCFury's native binding index in the layer-to-blendtree pass (1.1372.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1372.0",
            "LayerToTreeService.Apply now builds the per-layer binding sets and the inverted " +
            "binding→layers index once, instead of rescanning every layer per candidate.",
            "https://github.com/VRCFury/VRCFury/commit/fce41fe6123787a195283e5e0ee4410ec6fca0b0");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
