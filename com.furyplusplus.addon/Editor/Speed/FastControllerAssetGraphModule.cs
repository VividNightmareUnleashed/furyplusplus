using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. Finding an AnimatorController's unsaved sub-assets used to mean a
     * SerializedObject walk that inspected every serialized property on every state, state
     * machine, transition, behaviour, motion and mask. This module replaced that with a
     * traversal of Unity's public controller graph, which reaches the same objects without
     * the property-level scan, and deduplicated identical generated clips on the way past.
     *
     * VRCFury 1.1382.0 removed the walk rather than speeding it up. Controllers are built
     * as a detached model and saved through a VFSaveContext that accumulates NewAssets and
     * OtherAssets as it goes, so VFController.Save hands SaveAssetsSession the finished
     * asset lists instead of asking it to rediscover them. The clip-deduplication half now
     * lives in ClipDedupModule, which merges content-equal clips inside VFClip.Save. The
     * patch code is removed and the toggle stays (struck through).
     */
    internal sealed class FastControllerAssetGraphModule : Module<FastControllerAssetGraphModule> {

        internal override string Id => "fastControllerAssetGraph";
        internal override string DisplayName => "Fast controller asset graph";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Asset saving";
        internal override string Description =>
            "Superseded — VRCFury no longer re-walks controllers when saving (1.1382.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "Saving a controller now uses the asset list the build already collected, " +
            "instead of rediscovering it with a SerializedObject walk.",
            "https://github.com/VRCFury/VRCFury/commit/80a4845527ad7e7384e243edb4e8605984dd3c04");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
