using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED, on a measurement rather than a signature change.
     *
     * This module memoized AnimatorIterator.Motions/Clips/Trees.From(Motion), which at
     * 1.1367 was the single biggest uncached path in a build: every From() re-walked a live
     * BlendTree graph, marshalling `children` out of native objects on each hop, and every
     * pass re-walked the same graphs.
     *
     * At 1.1382.0 the walk is over the detached model — VFTree.children is a managed list of
     * VFTreeChild and VFClip holds its curves in memory — so there is no native round-trip
     * left. Measured on the reference avatar: 15,223 walks totalling 78.6 ms of a 3,734 ms
     * bake, i.e. 2.1%. A perfect cache could not save more than that, and a real one saves less
     * because misses still walk.
     *
     * Against that, the invalidation design cannot be carried over: it hung on four verified
     * BlendTree mutation choke points, of which BlendTreeExtensions.RewriteChildren and
     * AnimationClipExtensions.CopyData no longer exist, while mutation now happens through
     * VFTree.AddChild / VFTree.RewriteChildren / VFClip.SetCurves / VFState.motion. A missed
     * choke point is a stale cache — a silent wrong answer, which is why this module shipped
     * with shadow validation. Re-deriving that surface to win ~1% is a bad trade.
     *
     * VRCFury reached the same conclusion independently in 27b99ada, removing caching from
     * its own binding rewriter as "probably actually faster without it".
     */
    internal sealed class AnimatorIteratorMemoModule : Module<AnimatorIteratorMemoModule> {

        internal override string Id => "animatorIteratorMemo";
        internal override string DisplayName => "Motion graph traversal cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Superseded — VRCFury's motion graphs are in-memory now, and the walks measure " +
            "~2% of a bake.";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1382.0",
            "Motion graphs are walked in memory rather than marshalled out of native " +
            "BlendTrees, leaving too little to cache to justify the invalidation risk.",
            "https://github.com/VRCFury/VRCFury/commit/27b99adace660c04531754171c9845e9cd2e6d95");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
