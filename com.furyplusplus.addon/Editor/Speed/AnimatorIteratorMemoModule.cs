using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Memoizes AnimatorIterator.Motions/Clips/Trees.From(Motion) — the single biggest
     * uncached path in a VRCFury build: every pass (LayerToTree guards, RewriteParameters,
     * UpgradeWrongParamTypes, FixWriteDefaults, TreeFlattening, cleanup…) re-walks the
     * same motion graphs and allocates fresh immutable sets.
     *
     * Cache design (per the audited plan):
     *  - Motion-SUBGRAPH level only — layer/controller-level From() decomposes into
     *    per-state From(state.motion) which is never cached, so unhookable state.motion
     *    reassignments are picked up for free.
     *  - Targeted eviction at the four verified BlendTree-mutation choke points
     *    (VFBlendTree.Add, BlendTreeExtensions.RewriteChildren, MutableManager
     *    .RewriteInternals on a Motion, AnimationUtility.SetAnimationClipSettings) via a
     *    reverse member→roots index that the Motions result set provides for free.
     *  - Full clears at every action boundary and build begin/end as the backstop.
     *  - Cached immutable set instances are returned directly (all callers read-only).
     *  - Shadow validation: a sample of cache hits recomputes the walk and compares; any
     *    mismatch logs, flushes, and disables the cache for the session.
     */
    internal sealed class AnimatorIteratorMemoModule : Module<AnimatorIteratorMemoModule> {
        internal static readonly ModuleOption ShadowValidation = new ModuleOption(
            "shadowValidation", "Shadow-validate cache hits (recommended during soak)", true,
            "Recomputes ~1/64 of cache hits and verifies them; a mismatch disables the cache.");

        private static readonly ModuleOption[] AllOptions = { ShadowValidation };

        internal override string Id => "animatorIteratorMemo";
        internal override string DisplayName => "Motion graph traversal cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Caches VRCFury's repeated motion-graph walks (the biggest uncached cost in a " +
            "build), invalidated at every tree mutation.";

        internal override System.Collections.Generic.IReadOnlyList<ModuleOption> Options => AllOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            AnimatorIteratorMemoPatch.Install(harmony, compat);
        }

        internal override string ReportStats() {
            return AnimatorIteratorMemoPatch.LastStats;
        }
    }

    internal static class AnimatorIteratorMemoPatch {
        internal static string LastStats;

        [ThreadStatic] private static Dictionary<Motion, object> motionsCache;
        [ThreadStatic] private static Dictionary<Motion, object> clipsCache;
        [ThreadStatic] private static Dictionary<Motion, object> treesCache;
        [ThreadStatic] private static Dictionary<Motion, List<Motion>> reverseIndex;
        [ThreadStatic] private static bool shadow;
        [ThreadStatic] private static long hits;
        [ThreadStatic] private static long misses;
        private static long hitSampler;
        private static bool brokenThisSession;

        private static FieldInfo vfBlendTreeField;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var motionsType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Motions"), "AnimatorIterator+Motions");
            var clipsType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips"), "AnimatorIterator+Clips");
            var treesType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Trees"), "AnimatorIterator+Trees");
            var vfBlendTreeType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VFBlendTree"), "VF.Utils.VFBlendTree");
            var blendTreeExtType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.BlendTreeExtensions"), "VF.Utils.BlendTreeExtensions");
            var mutableManagerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.MutableManager"), "VF.Utils.MutableManager");

            MethodInfo FromOf(Type type, string what) {
                return ReflectionUtils.Demand(
                    type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .SingleOrDefault(method => method.Name == "From"
                                                   && method.GetParameters().Length == 1
                                                   && method.GetParameters()[0].ParameterType == typeof(Motion)),
                    what);
            }
            var motionsFrom = FromOf(motionsType, "Motions.From(Motion)");
            var clipsFrom = FromOf(clipsType, "Clips.From(Motion)");
            var treesFrom = FromOf(treesType, "Trees.From(Motion)");

            vfBlendTreeField = ReflectionUtils.Demand(
                vfBlendTreeType
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(field => field.FieldType == typeof(BlendTree)),
                "VFBlendTree.<tree>");
            var vfBlendTreeAdd = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfBlendTreeType, "Add",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(Motion)),
                "VFBlendTree.Add(Motion, ...)");
            // Two overloads exist (single-child + list); both mutate, patch both.
            var rewriteChildrenOverloads = blendTreeExtType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "RewriteChildren"
                                 && method.GetParameters().Length == 2
                                 && method.GetParameters()[0].ParameterType == typeof(BlendTree))
                .ToList();
            if (rewriteChildrenOverloads.Count == 0) {
                throw new MissingMemberException(
                    "VRCFury member not found: BlendTreeExtensions.RewriteChildren(BlendTree, Func)");
            }
            var rewriteInternals = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(mutableManagerType, "RewriteInternals",
                    method => method.GetParameters().Length == 2),
                "MutableManager.RewriteInternals(...)");
            // AnimationUtility.SetAnimationClipSettings is extern (unpatchable); VRCFury's
            // managed entry point for clip-settings changes (incl. additiveReferencePoseClip,
            // which is a graph edge) is AnimationClipExtensions.CopyData. Cross-action
            // changes are covered by the action-boundary clears regardless.
            var copyData = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(
                    ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions"), "CopyData",
                    method => method.GetParameters().Length == 2),
                "AnimationClipExtensions.CopyData(from, to)");

            harmony.Patch(
                compatibility.RunMain,
                prefix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(End))
            );
            harmony.Patch(
                compatibility.ActionCall,
                prefix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(ClearAll))
            );
            harmony.Patch(
                motionsFrom,
                prefix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(MotionsPrefix)),
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(MotionsPostfix))
            );
            harmony.Patch(
                clipsFrom,
                prefix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(ClipsPrefix)),
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(ClipsPostfix))
            );
            harmony.Patch(
                treesFrom,
                prefix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(TreesPrefix)),
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(TreesPostfix))
            );
            harmony.Patch(
                vfBlendTreeAdd,
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(VfTreeMutated))
            );
            foreach (var overload in rewriteChildrenOverloads) {
                harmony.Patch(
                    overload,
                    postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(TreeMutated))
                );
            }
            harmony.Patch(
                rewriteInternals,
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(InternalsRewritten))
            );
            harmony.Patch(
                copyData,
                postfix: new HarmonyMethod(typeof(AnimatorIteratorMemoPatch), nameof(ClipDataCopied))
            );
        }

        private static void Begin() {
            var module = AnimatorIteratorMemoModule.Instance;
            var enabled = module?.Enabled == true && !brokenThisSession;
            motionsCache = enabled ? new Dictionary<Motion, object>() : null;
            clipsCache = enabled ? new Dictionary<Motion, object>() : null;
            treesCache = enabled ? new Dictionary<Motion, object>() : null;
            reverseIndex = enabled ? new Dictionary<Motion, List<Motion>>() : null;
            shadow = enabled && Settings.IsOptionEnabled(module, AnimatorIteratorMemoModule.ShadowValidation);
            hits = 0;
            misses = 0;
        }

        private static Exception End(Exception __exception) {
            if (motionsCache != null) {
                LastStats = $"hits={hits} misses={misses}";
            }
            motionsCache = null;
            clipsCache = null;
            treesCache = null;
            reverseIndex = null;
            return __exception;
        }

        private static void ClearAll() {
            motionsCache?.Clear();
            clipsCache?.Clear();
            treesCache?.Clear();
            reverseIndex?.Clear();
        }

        // ---- memo prefixes/postfixes ----

        private static bool MotionsPrefix(Motion root, ref object __result) {
            var cache = motionsCache;
            if (cache == null || root == null) return true;
            if (!cache.TryGetValue(root, out var cached)) {
                misses++;
                return true;
            }
            if (shadow && (hitSampler++ & 63) == 0 && !ShadowCheck(root, cached)) return true;
            hits++;
            __result = cached;
            return false;
        }

        private static void MotionsPostfix(Motion root, object __result) {
            var cache = motionsCache;
            if (cache == null || root == null || __result == null) return;
            if (cache.ContainsKey(root)) return;
            cache[root] = __result;
            var reverse = reverseIndex;
            if (reverse == null) return;
            foreach (Motion member in (IEnumerable)__result) {
                if (member == null) continue;
                reverse.GetOrAddList(member).Add(root);
            }
        }

        private static bool ClipsPrefix(Motion root, ref object __result) {
            return DerivedPrefix(clipsCache, root, ref __result);
        }

        private static void ClipsPostfix(Motion root, object __result) {
            DerivedPostfix(clipsCache, root, __result);
        }

        private static bool TreesPrefix(Motion root, ref object __result) {
            return DerivedPrefix(treesCache, root, ref __result);
        }

        private static void TreesPostfix(Motion root, object __result) {
            DerivedPostfix(treesCache, root, __result);
        }

        private static bool DerivedPrefix(Dictionary<Motion, object> cache, Motion root, ref object __result) {
            if (cache == null || root == null) return true;
            if (!cache.TryGetValue(root, out var cached)) return true;
            hits++;
            __result = cached;
            return false;
        }

        private static void DerivedPostfix(Dictionary<Motion, object> cache, Motion root, object __result) {
            if (cache == null || root == null || __result == null) return;
            cache[root] = __result;
        }

        // ---- eviction ----

        private static void VfTreeMutated(object __instance) {
            if (motionsCache == null) return;
            try {
                Evict(vfBlendTreeField.GetValue(__instance) as Motion);
            } catch {
                ClearAll();
            }
        }

        private static void TreeMutated(BlendTree __0) {
            Evict(__0);
        }

        private static void InternalsRewritten(UnityEngine.Object __0) {
            if (motionsCache == null) return;
            if (__0 is Motion motion && reverseIndex != null && reverseIndex.ContainsKey(motion)) {
                // Reference rewriting can change edges anywhere — safest is a full drop.
                ClearAll();
            }
        }

        private static void ClipDataCopied(AnimationClip __0, AnimationClip __1) {
            Evict(__0);
            Evict(__1);
        }

        private static void Evict(Motion member) {
            var reverse = reverseIndex;
            if (reverse == null || member == null) return;
            // The member itself may be a cached root too.
            motionsCache?.Remove(member);
            clipsCache?.Remove(member);
            treesCache?.Remove(member);
            if (!reverse.TryGetValue(member, out var roots)) return;
            foreach (var root in roots) {
                motionsCache?.Remove(root);
                clipsCache?.Remove(root);
                treesCache?.Remove(root);
            }
            roots.Clear();
        }

        // ---- shadow validation ----

        private static bool ShadowCheck(Motion root, object cached) {
            try {
                var recomputed = new HashSet<object>();
                var stack = new Stack<Motion>();
                stack.Push(root);
                while (stack.Count > 0) {
                    var one = stack.Pop();
                    if (one == null || recomputed.Contains(one)) continue;
                    recomputed.Add(one);
                    if (one is BlendTree tree) {
                        foreach (var child in tree.children) stack.Push(child.motion);
                    } else if (one is AnimationClip clip) {
                        var settings = AnimationUtility.GetAnimationClipSettings(clip);
                        if (settings.additiveReferencePoseClip != null) {
                            stack.Push(settings.additiveReferencePoseClip);
                        }
                    }
                }
                var cachedSet = ((IEnumerable)cached).Cast<object>().ToHashSet();
                if (recomputed.SetEquals(cachedSet)) return true;

                brokenThisSession = true;
                ClearAll();
                motionsCache = null;
                clipsCache = null;
                treesCache = null;
                Log.Warn("Motion graph cache failed shadow validation and is disabled for this " +
                         $"session (root: {root.name}).");
                return false;
            } catch {
                return true; // never let the validator itself break a hit
            }
        }
    }
}
