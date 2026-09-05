using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Upgrades two-sided layer-to-blendtree conversions to the cheaper one-sided form: a
     * converted toggle's OFF clip is sampled and written EVERY frame the toggle is off.
     * When every off-value equals the avatar's rest value (which the WD defaults layer
     * already writes) and no OTHER FX layer — any priority, stricter than VRCFury's own
     * ≥-only check — nor any non-FX controller animates those bindings, the off clip is
     * replaced with an empty clip before VRCFury's Optimize runs; its own existing
     * one-sided branch then does the conversion.
     *
     * The global no-op strip already handles off clips whose bindings nothing else writes;
     * this covers the remaining case where the SAME layer's on-clip animates them. The
     * "constant write at rest" rules themselves are NoOpCurveStripPass.IsConstant/
     * ValuesMatch — one doctrine for both modules.
     *
     * As of VRCFury 1.1382 LayerToTreeService.Apply builds the per-layer binding index and
     * the reverse binding→layers map itself and threads both into OptimizeLayer, so this
     * module reads them off the call instead of walking every FX layer up front. Only the
     * non-FX conflict scan is still ours — those controllers are outside the maps.
     *
     * Note ClipFactoryService is [VFPrototypeScope]: it cannot be resolved as a singleton
     * from the injector, because the clip names it mints depend on which builder asked for
     * it. That is why the off side is nulled rather than replaced — Optimize then fills it
     * from its own correctly parented factory.
     */
    internal sealed class OffSideEliminationModule : Module<OffSideEliminationModule> {
        internal override string Id => "offSideElimination";
        internal override string DisplayName => "One-sided blendtree toggles";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Animator layers";
        internal override string Description =>
            "Converts blendtree toggles whose off state only writes resting values to the " +
            "one-sided form — no per-frame writes while the toggle is off.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            OffSideEliminationPatch.Install(harmony, compat);
        }

        internal override string ReportStats() {
            return OffSideEliminationPatch.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return OffSideEliminationPatch.LastUpgraded > 0
                ? ($"{OffSideEliminationPatch.LastUpgraded} toggles one-sided last bake",
                    OffSideEliminationPatch.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class OffSideEliminationPatch {
        internal static string LastStats;
        internal static int LastUpgraded;

        // Rebuilt at the LayerToTree boundary each run; null outside it. Non-null doubles as
        // the "module enabled and context healthy" signal for the hot prefixes.
        [ThreadStatic] private static HashSet<object> conflictingBindings;
        [ThreadStatic] private static object defaultLayer;
        [ThreadStatic] private static int upgraded;
        // Off-sides we actually got to look at, and layers VRCFury got as far as converting.
        // Without these, "0 upgrades" is ambiguous between "nothing was eligible" and "the
        // patch never ran".
        [ThreadStatic] private static int considered;
        [ThreadStatic] private static int layersSeen;

        // Captured per OptimizeLayer call, straight off VRCFury's own arguments.
        [ThreadStatic] private static object currentLayer;
        [ThreadStatic] private static IDictionary layersByBinding;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetAllCurves, "VFClip.GetAllCurves()");
            ReflectionUtils.Demand(ClipCurveCompat.BindingNormalize, "VFBinding.Normalize(combineRotation)");
            ReflectionUtils.Demand(ClipCurveCompat.BindingTryGetCurrentFloat,
                "VFBinding.TryGetCurrentFloat(root, out value)");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(ToggleTreeCompat.GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");

            // OptimizeLayer(layer, bindingsByLayer, layersByBinding, directTree)
            var optimizeLayer = ReflectionUtils.Demand(
                ToggleTreeCompat.OptimizeLayer,
                "LayerToTreeService.OptimizeLayer(layer, bindingsByLayer, layersByBinding, tree)");
            if (!typeof(IDictionary).IsAssignableFrom(optimizeLayer.GetParameters()[2].ParameterType)) {
                throw new MissingMemberException(
                    "LayerToTreeService.OptimizeLayer layersByBinding dictionary signature");
            }

            // Optimize(condition, on, off, directTree)
            var optimize = ReflectionUtils.Demand(
                ToggleTreeCompat.Optimize,
                "LayerToTreeService.Optimize(condition, on, off, tree)");
            // (AnimatorCondition, VFMotion on, VFMotion off, …) — the condition drives which
            // of the two motions survives normalization as the off side, so its type matters
            // as much as the motions'.
            var optimizeParams = optimize.GetParameters();
            var motionType = ClipCurveCompat.ClipType.BaseType;
            if (optimizeParams[0].ParameterType != typeof(AnimatorCondition)
                || optimizeParams[1].ParameterType != motionType
                || optimizeParams[2].ParameterType != motionType) {
                throw new MissingMemberException(
                    "LayerToTreeService.Optimize(AnimatorCondition, VFMotion, VFMotion, tree)");
            }

            harmony.Patch(
                ReflectionUtils.Demand(ToggleTreeCompat.LayerToTreeApply, "LayerToTreeService.Apply()"),
                prefix: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(PrepareForRun)),
                finalizer: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(EndRun))
            );

            harmony.Patch(
                optimizeLayer,
                prefix: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(OptimizeLayerPrefix)),
                finalizer: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(OptimizeLayerFinalizer))
            );
            harmony.Patch(
                optimize,
                prefix: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(OptimizePrefix))
            );
        }

        /**
         * Builds the cross-controller conflict data once per build, just before LayerToTree
         * runs. Only non-FX controllers are scanned here — VRCFury indexes the FX layers
         * itself and hands us the result per layer.
         */
        private static void PrepareForRun() {
            conflictingBindings = null;
            defaultLayer = null;
            currentLayer = null;
            layersByBinding = null;
            upgraded = 0;
            considered = 0;
            layersSeen = 0;
            LastUpgraded = 0;
            LastStats = null;
            if (OffSideEliminationModule.Instance?.Enabled != true) return;

            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            if (controllersService == null || fixWd == null) {
                LastStats = "no services: "
                            + (controllersService == null ? "controllers " : "")
                            + (fixWd == null ? "fixWd" : "");
                return;
            }

            try {
                // Bindings animated by NON-FX controllers always conflict (they must keep
                // their own timing/override semantics).
                var conflicts = new HashSet<object>();
                var fx = ToggleTreeCompat.GetFx.Invoke(controllersService, null);
                foreach (var manager in ClipCurveCompat.UsedControllers(controllersService)) {
                    if (ReferenceEquals(manager, fx)) continue;
                    foreach (var clip in ClipCurveCompat.ClipsFrom(manager)) {
                        if (clip == null) continue;
                        foreach (var binding in ClipCurveCompat.AllBindingsOf(clip)) {
                            conflicts.Add(ClipCurveCompat.Normalize(binding, true));
                        }
                    }
                }

                conflictingBindings = conflicts;
                defaultLayer = ToggleTreeCompat.GetDefaultLayer.Invoke(fixWd, null);
                LastStats = "armed, no candidate layers";
            } catch (Exception e) {
                conflictingBindings = null;
                defaultLayer = null;
                Log.Warn("Off-side elimination fell back to VRCFury: " + e.Message);
            }
        }

        private static Exception EndRun(Exception __exception) {
            conflictingBindings = null;
            defaultLayer = null;
            currentLayer = null;
            layersByBinding = null;
            return __exception;
        }

        // __0 is the layer being converted, __2 the binding→layers reverse index. Both stay
        // live only for the duration of this one OptimizeLayer call.
        private static void OptimizeLayerPrefix(object __0, object __2) {
            currentLayer = __0;
            layersByBinding = __2 as IDictionary;
            if (conflictingBindings == null) return;
            layersSeen++;
            LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";
        }

        private static Exception OptimizeLayerFinalizer(Exception __exception) {
            currentLayer = null;
            layersByBinding = null;
            return __exception;
        }

        /**
         * Nulls out the off-side motion, which routes VRCFury's own code down its existing
         * one-sided branch: Optimize fills a null side from its (prototype-scoped, correctly
         * parented) clip factory, and the resulting empty clip fails HasValidBinding.
         *
         * Which argument IS the off side depends on the condition: Optimize normalizes
         * IfNot/Less/NotEqual by swapping the two motions, so for those the third argument
         * ends up as the ON side and emptying it would silently delete the toggle's content.
         * Pick the side that survives normalization.
         *
         * The ThreadStatic context doubles as the enabled signal — PrepareForRun leaves it
         * null when the module is off.
         */
        private static void OptimizePrefix(AnimatorCondition __0, ref object __1, ref object __2) {
            if (conflictingBindings == null) return;
            if (currentLayer == null || layersByBinding == null) return;

            var swaps = __0.mode == AnimatorConditionMode.IfNot
                        || __0.mode == AnimatorConditionMode.Less
                        || __0.mode == AnimatorConditionMode.NotEqual;

            try {
                var offClip = swaps ? __1 : __2;
                if (offClip == null) return;
                // A blendtree off-side is not a clip; leave those to stock conversion.
                if (!ClipCurveCompat.ClipType.IsInstanceOfType(offClip)) return;

                var avatarRoot = BuildPhaseHooks.CurrentAvatarRoot;
                if (avatarRoot == null) return;
                var vfAvatarRoot = ClipCurveCompat.WrapGameObject(avatarRoot);
                if (vfAvatarRoot == null) return;

                var curves = ClipCurveCompat.AllCurvesOf(offClip);
                if (curves.Length == 0) return;

                considered++;
                LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";

                foreach (var entry in curves) {
                    var binding = ClipCurveCompat.TupleBinding(entry);
                    var curve = ClipCurveCompat.TupleCurve(entry);

                    // AAPs and muscles have no resting value to fall back to.
                    if (ClipCurveCompat.IsAnimatorBinding(binding)) return;

                    // VRCFury indexes its layers under the rotation-combined normal form.
                    var normalized = ClipCurveCompat.Normalize(binding, true);
                    if (conflictingBindings.Contains(normalized)) return;

                    // Stock two-sided conversion writes the rest value OVER lower-priority
                    // layers while off; one-sided must not change that, so the candidate
                    // layer has to be the only writer. The defaults layer is exempt: what it
                    // writes IS the rest value, which is exactly what we substitute. A
                    // binding missing from the index was filtered as invalid — bail.
                    if (!(layersByBinding[normalized] is IEnumerable writers)) return;
                    foreach (var writer in writers) {
                        if (ReferenceEquals(writer, currentLayer)) continue;
                        if (defaultLayer != null && ReferenceEquals(writer, defaultLayer)) continue;
                        return;
                    }

                    if (curve == null || !ClipCurveCompat.IsFloat(curve)) return;
                    var floatCurve = ClipCurveCompat.FloatCurveOf(curve);
                    if (floatCurve == null || !NoOpCurveStripPass.IsConstant(floatCurve, out var value)) return;
                    if (!ClipCurveCompat.TryGetRestValue(binding, vfAvatarRoot, out var rest)) return;
                    if (!NoOpCurveStripPass.ValuesMatch(
                            ClipCurveCompat.PropertyNameOf(binding), value, rest)) return;
                }

                if (swaps) __1 = null; else __2 = null;
                upgraded++;
                LastUpgraded = upgraded;
                LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";
            } catch {
                // Leave the motion untouched — stock two-sided conversion proceeds.
            }
        }
    }
}
