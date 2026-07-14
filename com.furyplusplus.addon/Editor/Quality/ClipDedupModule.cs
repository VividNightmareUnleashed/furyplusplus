using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Controller-wide dedup of VRCFury-generated clips: identical curve sets + settings
     * collapse to one shared instance before SaveAssets, so the saved controller references
     * (and the upload ships) each unique clip once. VRCFury's own merging only operates
     * within a single direct blendtree; identical generated clips across layers/states stay
     * separate without this.
     *
     * Conservative: only clips VRCFury generated or changed (GetUseOriginalUserClip == null)
     * and non-proxy clips participate; the identity key is the shared ClipContentKey
     * serialization (every curve key facet + full clip settings), so differing loop/length
     * can never merge. First occurrence in controller/layer order wins.
     */
    internal sealed class ClipDedupModule : Module<ClipDedupModule> {
        internal override string Id => "clipDedup";
        internal override string DisplayName => "Deduplicate generated clips (controller-wide)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Animation clips";
        internal override string Description =>
            "Points identical VRCFury-generated animation clips at one shared instance " +
            "across all layers and blendtrees before assets are saved.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ClipDedupPass.Resolve();
            BuildPhaseHooks.RegisterBefore("SaveAssets", Id, _ => ClipDedupPass.Run());
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

        internal static void Resolve() {
            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ControllerCtrlField, "VFController.ctrl");
            ReflectionUtils.Demand(ClipCurveCompat.GetUseOriginalUserClip,
                "AnimationClipExtensions.GetUseOriginalUserClip(clip)");
            ReflectionUtils.Demand(ClipCurveCompat.CurveObjectCurve, "FloatOrObjectCurve.ObjectCurve");
        }

        internal static void Run() {
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            if (controllersService == null) return;

            // Discover canonical clips in deterministic controller/layer order.
            var canonicalByHash = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            var replacement = new Dictionary<AnimationClip, AnimationClip>();
            var managers = ((IEnumerable)ClipCurveCompat.GetAllUsedControllers
                    .Invoke(controllersService, null))
                .Cast<object>().ToList();
            var controllers = managers
                .Select(ClipCurveCompat.RawController)
                .Where(controller => controller != null)
                .ToList();

            void Discover(AnimationClip clip) {
                if (clip == null || replacement.ContainsKey(clip)) return;
                if (ClipCurveCompat.IsProxyClip(clip)) return;
                if (ClipCurveCompat.GetUseOriginalUserClip.Invoke(null, new object[] { clip }) != null) return;
                string hash;
                try {
                    hash = HashClip(clip);
                } catch {
                    return; // unhashable clip: leave it alone
                }
                if (hash == null) return;
                if (canonicalByHash.TryGetValue(hash, out var canonical)) {
                    if (!ReferenceEquals(canonical, clip)) replacement[clip] = canonical;
                } else {
                    canonicalByHash[hash] = clip;
                }
            }

            void WalkMotionDiscover(Motion motion, HashSet<Motion> seen) {
                if (motion == null || !seen.Add(motion)) return;
                if (motion is AnimationClip clip) {
                    Discover(clip);
                } else if (motion is BlendTree tree) {
                    foreach (var child in tree.children) WalkMotionDiscover(child.motion, seen);
                }
            }

            var seenMotions = new HashSet<Motion>();
            foreach (var controller in controllers) {
                foreach (var layer in controller.layers) {
                    WalkStates(layer.stateMachine, state => WalkMotionDiscover(state.motion, seenMotions));
                }
            }

            if (replacement.Count == 0) {
                LastStats = null;
                LastDuplicates = 0;
                return;
            }

            // Repoint every reference (public animator API only).
            var repointed = 0;
            foreach (var controller in controllers) {
                foreach (var layer in controller.layers) {
                    WalkStates(layer.stateMachine, state => {
                        if (state.motion is AnimationClip clip && replacement.TryGetValue(clip, out var canon)) {
                            state.motion = canon;
                            repointed++;
                        } else if (state.motion is BlendTree tree) {
                            repointed += RepointTree(tree, replacement, new HashSet<BlendTree>());
                        }
                    });
                }
            }

            Log.Info($"Deduplicated {replacement.Count} identical generated clip(s) " +
                     $"({repointed} reference(s) repointed).");
            LastDuplicates = replacement.Count;
            LastStats = $"duplicates={replacement.Count} repointed={repointed}";
        }

        private static void WalkStates(AnimatorStateMachine machine, Action<AnimatorState> visit) {
            if (machine == null) return;
            foreach (var child in machine.states) {
                if (child.state != null) visit(child.state);
            }
            foreach (var child in machine.stateMachines) {
                WalkStates(child.stateMachine, visit);
            }
        }

        private static int RepointTree(BlendTree tree, Dictionary<AnimationClip, AnimationClip> replacement, HashSet<BlendTree> seen) {
            if (!seen.Add(tree)) return 0;
            var repointed = 0;
            var children = tree.children;
            var changed = false;
            for (var i = 0; i < children.Length; i++) {
                if (children[i].motion is AnimationClip clip && replacement.TryGetValue(clip, out var canon)) {
                    children[i].motion = canon;
                    changed = true;
                    repointed++;
                } else if (children[i].motion is BlendTree childTree) {
                    repointed += RepointTree(childTree, replacement, seen);
                }
            }
            if (changed) tree.children = children;
            return repointed;
        }

        /** Null = the clip cannot be hashed faithfully; leave it out of the dedup. */
        private static string HashClip(AnimationClip clip) {
            var builder = new StringBuilder();
            ClipContentKey.AppendClipFacts(builder, clip);

            var entries = new List<(EditorCurveBinding Binding, object Curve)>();
            foreach (var entry in ClipCurveCompat.AllCurvesOf(clip)) {
                entries.Add((ClipCurveCompat.TupleBinding(entry), ClipCurveCompat.TupleCurve(entry)));
            }
            ClipContentKey.SortByBinding(entries, entry => entry.Binding);
            foreach (var entry in entries) {
                if (!ClipContentKey.TryAppendCurve(builder, entry.Binding, entry.Curve)) return null;
            }
            return Hash128.Compute(builder.ToString()).ToString();
        }
    }
}
