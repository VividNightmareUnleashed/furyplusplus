using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. This module kept VRCFury's outer AssetDatabase.StartAssetEditing batch
     * active while SaveAssetsService created generated assets, avoiding an import cycle
     * for every saved root on Unity 2022.
     *
     * VRCFury 1.1394.0 rewrote the save lifecycle so SaveAssets no longer exits and
     * re-enters the build-wide asset-editing scope. The focused Unity 2022 comparison
     * found no SaveAssetsService.Run → WithoutAssetEditing calls, and stock 1.1394.0 was
     * at least as fast with this patch disabled. The patch code is removed and the toggle
     * stays (struck through).
     */
    internal sealed class SaveAssetsBatchingModule : Module<SaveAssetsBatchingModule> {

        internal override string Id => "saveAssetsBatching";
        internal override string DisplayName => "SaveAssets batching (Unity 2022)";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Asset saving";
        internal override string Description =>
            "Superseded by VRCFury's build-wide asset-editing scope (1.1394.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1394.0",
            "SaveAssets stays inside VRCFury's build-wide asset-editing scope without a " +
            "SaveAssets-specific leave/re-enter cycle.",
            "https://github.com/VRCFury/VRCFury/commit/4dab459c16ba4e2abb2d11ff473e34157c3bf068");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
