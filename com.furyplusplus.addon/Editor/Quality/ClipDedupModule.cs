using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Controller-wide dedup of VRCFury-generated clips: identical curve sets + settings
     * collapse to one shared asset, so the saved controller references (and the upload
     * ships) each unique clip once. VRCFury's own merging only operates within a single
     * direct blendtree; identical generated clips across layers/states stay separate
     * without this.
     *
     * As of VRCFury 1.1382 clips are detached VFClip objects that only become assets in
     * VFClip.Save, and VFSaveContext memoizes them by object identity — two content-equal
     * VFClips still produce two assets. So the dedup now happens inside Save itself: the
     * first VFClip of a given content key saves normally, and any later content-equal clip
     * returns that same asset instead of writing its own. Nothing is created and then
     * repointed, and the in-memory controller graph is never touched.
     *
     * Conservative: only clips VRCFury generated or changed participate (a clip still
     * eligible to alias its original user asset is left alone), never proxies, and the
     * identity key is the shared ClipContentKey serialization of the bindings exactly as
     * they will be written — so differing loop/length/settings can never merge. Clip names
     * are deliberately NOT part of the key (that is what makes the common case — the same
     * generated clip under different toggle names — merge at all); the first occurrence in
     * save order wins and its name survives. Scope is one save context, i.e. one
     * controller, which is also the one asset file a shared clip could live in.
     */
    internal sealed class ClipDedupModule : Module<ClipDedupModule> {
        internal override string Id => "clipDedup";
        internal override string DisplayName => "Deduplicate generated clips (controller-wide)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Animation clips";
        internal override string Description =>
            "Saves identical VRCFury-generated animation clips as one shared asset instead " +
            "of one per layer or blendtree slot.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ClipDedupPass.Install(harmony);
        }

        internal override string ReportStats() {
            return ClipDedupPass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return ClipDedupPass.LastDuplicates > 0
                ? ($"{ClipDedupPass.LastDuplicates} duplicate clips removed last bake", ClipDedupPass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class ClipDedupPass {
        internal static string LastStats;
        internal static int LastDuplicates;

        // One save context == one controller == one output asset file, so the canonical
        // table is scoped to it. Contexts never interleave (Save is a depth-first walk).
        [ThreadStatic] private static object activeContext;
        [ThreadStatic] private static Dictionary<string, Motion> canonicalByHash;
        [ThreadStatic] private static Dictionary<object, string> pendingHash;
        [ThreadStatic] private static int duplicates;
        [ThreadStatic] private static bool enabledForContext;
        [ThreadStatic] private static Dictionary<object, int> additiveIds;

        private sealed class ReferenceComparer : IEqualityComparer<object> {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        internal static void Install(Harmony harmony) {
            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetUseOriginalUserClip,
                "VFClip.GetUseOriginalUserClip(root)");
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetSourceAsset, "VFMotion.GetSourceAsset()");
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetLengthInSeconds, "VFClip.GetLengthInSeconds()");
            ReflectionUtils.Demand(ClipCurveCompat.ClipIsLooping, "VFClip.IsLooping()");
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetAdditiveRefPose,
                "VFClip.GetAdditiveReferencePoseClip()");
            ReflectionUtils.Demand(ClipCurveCompat.ClipFrameRate, "VFClip.frameRate");
            ReflectionUtils.Demand(ClipCurveCompat.BindingToEditorCurveBinding,
                "VFBinding.ToEditorCurveBinding(root)");
            ReflectionUtils.Demand(ClipCurveCompat.CurveObjectCurve, "FloatOrObjectCurve.ObjectCurve");

            var save = ReflectionUtils.Demand(
                ClipCurveCompat.ClipSave,
                "VFClip.Save(VFSaveContext)");
            ReflectionUtils.Demand(ClipCurveCompat.SaveContextBindingRoot, "VFSaveContext.BindingRoot");
            ReflectionUtils.Demand(ClipCurveCompat.SaveContextReuseSource,
                "VFSaveContext.ReuseSourceAssets");

            harmony.Patch(
                save,
                prefix: new HarmonyMethod(typeof(ClipDedupPass), nameof(SavePrefix)),
                postfix: new HarmonyMethod(typeof(ClipDedupPass), nameof(SavePostfix))
            );
        }

        /** False = a content-equal clip already saved; hand back its asset and skip the write. */
        private static bool SavePrefix(object __instance, object __0, ref Motion __result) {
            string hash;
            try {
                if (!ReferenceEquals(activeContext, __0)) {
                    activeContext = __0;
                    enabledForContext = ClipDedupModule.Instance?.Enabled == true;
                    canonicalByHash = enabledForContext
                        ? new Dictionary<string, Motion>(StringComparer.Ordinal)
                        : null;
                    pendingHash = enabledForContext ? new Dictionary<object, string>() : null;
                    additiveIds = enabledForContext
                        ? new Dictionary<object, int>(ReferenceComparer.Instance) : null;
                    duplicates = 0;
                }
                if (!enabledForContext) return true;
                if (!IsEligible(__instance, __0)) return true;
                hash = HashClip(__instance, __0);
                if (hash == null) return true;
            } catch (Exception e) {
                Log.Warn("Clip dedup fell back to VRCFury: " + e.Message);
                return true;
            }

            if (canonicalByHash.TryGetValue(hash, out var canonical)) {
                __result = canonical;
                duplicates++;
                LastDuplicates = duplicates;
                LastStats = $"duplicates={duplicates}";
                return false;
            }
            pendingHash[__instance] = hash;
            return true;
        }

        private static void SavePostfix(object __instance, object __0, Motion __result) {
            if (pendingHash == null || __result == null) return;
            if (!ReferenceEquals(activeContext, __0)) return;
            if (!pendingHash.TryGetValue(__instance, out var hash)) return;
            pendingHash.Remove(__instance);
            canonicalByHash[hash] = __result;
        }

        /**
         * Clips that can still alias the user's own asset are off limits — Save hands back
         * that very asset, and merging two of those would drop one user clip's identity.
         * Proxy clips are VRChat's, never ours to share.
         */
        private static bool IsEligible(object clip, object context) {
            if (ClipCurveCompat.IsProxyClip(clip)) return false;
            if (!(ClipCurveCompat.SaveContextReuseSource.GetValue(context) is bool reuse) || !reuse) return true;
            var bindingRoot = ClipCurveCompat.SaveContextBindingRoot.GetValue(context);
            if (bindingRoot == null) return false;
            return ClipCurveCompat.GetUseOriginalUserClip(clip, bindingRoot) == null;
        }

        /** Null = the clip cannot be hashed faithfully; leave it out of the dedup. */
        private static string HashClip(object clip, object context) {
            var bindingRoot = ClipCurveCompat.SaveContextBindingRoot.GetValue(context);
            if (bindingRoot == null) return null;

            var builder = new StringBuilder();

            // Save either clones the original source asset (carrying its settings, events,
            // bounds and wrap mode) or starts from a fresh clip — hash whichever base it is.
            if (ClipCurveCompat.ClipGetSourceAsset.Invoke(clip, null) is AnimationClip source) {
                ClipContentKey.AppendClipFacts(builder, source);
            } else {
                builder.Append("clip|fresh").AppendLine();
            }

            // The in-memory state Save writes over that base.
            builder.Append("rate|").Append(((float)ClipCurveCompat.ClipFrameRate.GetValue(clip))
                    .ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(ClipCurveCompat.ClipIsLooping.Invoke(clip, null)).Append('|')
                .Append(((float)ClipCurveCompat.ClipGetLengthInSeconds.Invoke(clip, null))
                    .ToString("R", CultureInfo.InvariantCulture)).AppendLine();

            // The additive reference pose is saved as its own asset and referenced by
            // settings, so only clips pointing at the same one may merge.
            var additive = ClipCurveCompat.ClipGetAdditiveRefPose.Invoke(clip, null);
            if (additive != null && !additiveIds.ContainsKey(additive)) additiveIds[additive] = additiveIds.Count;
            builder.Append("additive|")
                .Append(additive == null ? "<null>" : additiveIds[additive].ToString()).AppendLine();

            var entries = new List<(EditorCurveBinding Binding, object Curve)>();
            foreach (var entry in ClipCurveCompat.AllCurvesOf(clip)) {
                var binding = ClipCurveCompat.TupleBinding(entry);
                entries.Add((
                    ClipCurveCompat.ToEditorCurveBinding(binding, bindingRoot),
                    ClipCurveCompat.TupleCurve(entry)
                ));
            }
            ClipContentKey.SortByBinding(entries, entry => entry.Binding);
            foreach (var entry in entries) {
                if (!ClipContentKey.TryAppendCurve(builder, entry.Binding, entry.Curve)) return null;
            }
            return Hash128.Compute(builder.ToString()).ToString();
        }
    }
}
