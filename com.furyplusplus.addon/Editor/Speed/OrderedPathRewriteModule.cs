using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. ObjectMoveService used to queue every move as an (oldPath, newPath) pair and
     * then rewrite each one across every managed clip, scanning the whole deferred list per
     * path; this replaced that mapping lambda with a trie-based chronological resolver.
     * VRCFury 1.1372.0 reworked its path caches so animation targeting and Armature Link no
     * longer use direct path lookups at all — bindings retarget through VFResolvedObject /
     * VRCFObjectPathCache, and the deferred list, ApplyDeferred and the string rewrite were
     * deleted outright. There is no rewrite pass left to accelerate; the patch code and its
     * resolver are removed and the toggle stays (struck through) pointing at the upstream
     * commit.
     */
    internal sealed class OrderedPathRewriteModule : Module<OrderedPathRewriteModule> {

        internal override string Id => "orderedPathRewrite";
        internal override string DisplayName => "Ordered path rewrite";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Paths & rewriting";
        internal override string Description =>
            "Superseded by VRCFury's reworked path caches (1.1372.0), which removed deferred path rewriting.";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1372.0",
            "VRCFury retargets bindings through VFResolvedObject instead of rewriting path strings; the deferred rewrite pass is gone.",
            "https://github.com/VRCFury/VRCFury/commit/92c72f54f8533d101ffc50681ab0463e5d4a747d");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
