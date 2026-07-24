using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. SaveAssets used to finish by scanning EVERY component on the avatar for
     * unsaved generated sub-assets, re-scanning components it had already visited. This
     * module preserved the renderer and controller passes but replaced that final sweep
     * with a pass over VrcfObjectFactory's created objects, and carried opt-in toggles to
     * skip inert Transform scans and repeated Renderer scans.
     *
     * VRCFury 1.1382.0 narrowed the sweep itself: SaveAssetsService.Run now visits only the
     * avatar descriptor, Renderers, MeshFilters and AudioSources, and SaveAssetsSession
     * keeps a scannedComponents set so no component is walked twice. Both hand-rolled skip
     * options describe what upstream now does unconditionally, so the patch code is removed
     * and the toggle stays (struck through).
     *
     * This module used to carry an "overrides VRCFury" note against the 1.1364 native
     * dedup. That no longer applies — the pass it overrode has itself been replaced.
     */
    internal sealed class SaveAssetsDiscoveryModule : Module<SaveAssetsDiscoveryModule> {

        internal override string Id => "saveAssetsDiscovery";
        internal override string DisplayName => "Fast SaveAssets discovery";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Asset saving";
        internal override string Description =>
            "Superseded by VRCFury's narrowed asset-saving scan (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "SaveAssets now scans only the avatar, Renderers, MeshFilters and AudioSources, " +
            "and never scans the same component twice.",
            "https://github.com/VRCFury/VRCFury/commit/6d4d5ce57dbe5cb8a58b820cf298377e779c2b6a");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
