using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. VRCFury used to write a separate asset file for every generated material,
     * mesh, texture, menu and parameter root, and Unity spends far more time importing
     * files than it does attaching sub-assets. This module kept controllers as their own
     * .controller assets and attached every other generated root to one container file per
     * SaveAssets pass.
     *
     * VRCFury 1.1382.0 does exactly that itself: SaveAssetsService.Run creates a single
     * "VRCFury Other" BinaryContainer on demand and SaveOtherAssetAndChildren attaches the
     * non-controller assets to it rather than saving each as its own file. The patch code
     * is removed and the toggle stays (struck through).
     */
    internal sealed class ConsolidatedAssetContainerModule : Module<ConsolidatedAssetContainerModule> {

        internal override string Id => "consolidatedAssetContainer";
        internal override string DisplayName => "Consolidated asset container";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Asset saving";
        internal override string Description =>
            "Superseded by VRCFury's own shared asset container (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "Generated non-controller assets are attached to one shared \"VRCFury Other\" " +
            "container instead of being imported as a file each.",
            "https://github.com/VRCFury/VRCFury/commit/6d4d5ce57dbe5cb8a58b820cf298377e779c2b6a");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
