using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * Upgrades two-sided layer-to-blendtree conversions to the cheaper one-sided form: a
     * converted toggle's OFF clip is sampled and written EVERY frame the toggle is off.
     * When every value one side writes equals the avatar's rest value (which the WD
     * defaults layer already writes) and no OTHER FX layer (any priority, stricter than
     * VRCFury's own ≥-only check) nor any non-FX controller animates those bindings, that
     * side is dropped. With nothing written there, the bindings fall back to the same rest
     * values, so the toggle behaves the same while it no longer writes them every frame.
     *
     * VRCFury 1.1443.0 rewrote the two-state conversion: it now builds one condition tree
     * from all transition conditions and has no separate Optimize(condition, on, off) step.
     * Both the old and the new conversion read each state's motion through
     * Make0LengthClipForState(layer, state), so this module drops the rest-only side there
     * by returning null; VRCFury fills a null side with an empty clip. At most one side per
     * layer is dropped, so the other side keeps the layer convertible.
     *
     * The global no-op strip already handles off clips whose bindings nothing else writes;
     * this covers the remaining case where the SAME layer's other state animates them. The
     * "constant write at rest" rules themselves are NoOpCurveStripPass.IsConstant/
     * ValuesMatch — one doctrine for both modules.
     *
     * As of VRCFury 1.1382 LayerToTreeService.Apply builds the per-layer binding index and
     * the reverse binding→layers map itself and threads both into OptimizeLayer, so this
     * module reads them off the call instead of walking every FX layer up front. Only the
     * non-FX conflict scan is still ours — those controllers are outside the maps.
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
        // the "module enabled and context healthy" signal for the hot patches.
        [ThreadStatic] private static HashSet<object> conflictingBindings;
        [ThreadStatic] private static object defaultLayer;
        [ThreadStatic] private static int upgraded;
        // Sides we actually got to look at, and layers VRCFury got as far as converting.
        // Without these, "0 upgrades" is ambiguous between "nothing was eligible" and "the
        // patch never ran".
        [ThreadStatic] private static int considered;
        [ThreadStatic] private static int layersSeen;

        // Captured per OptimizeLayer call, straight off VRCFury's own arguments.
        [ThreadStatic] private static object currentLayer;
        [ThreadStatic] private static IDictionary layersByBinding;
        [ThreadStatic] private static bool droppedSideInLayer;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetAllCurves, "VFClip.GetAllCurves()");
            ReflectionUtils.Demand(ClipCurveCompat.BindingNormalize, "VFBinding.Normalize(combineRotation)");
            ReflectionUtils.Demand(ClipCurveCompat.BindingTryGetCurrentFloat,
                "VFBinding.TryGetCurrentFloat(root, out value)");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(ToggleTreeCompat.GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");
            ReflectionUtils.Demand(ToggleTreeCompat.LayerStateMachine, "VFLayer.stateMachine");
            ReflectionUtils.Demand(ToggleTreeCompat.SmStates, "VFStateMachine.states");
            ReflectionUtils.Demand(ToggleTreeCompat.StateMotion, "VFState.motion");

            // OptimizeLayer(layer, bindingsByLayer, layersByBinding, directTree)
            var optimizeLayer = ReflectionUtils.Demand(
                ToggleTreeCompat.OptimizeLayer,
                "LayerToTreeService.OptimizeLayer(layer, bindingsByLayer, layersByBinding, tree)");
            if (!typeof(IDictionary).IsAssignableFrom(optimizeLayer.GetParameters()[2].ParameterType)) {
                throw new MissingMemberException(
                    "LayerToTreeService.OptimizeLayer layersByBinding dictionary signature");
            }

            // VFMotion Make0LengthClipForState(VFLayer layer, VFState state)
            var makeClip = ReflectionUtils.Demand(
                ToggleTreeCompat.Make0LengthClipForState,
                "LayerToTreeService.Make0LengthClipForState(layer, state)");
            if (makeClip.ReturnType != ClipCurveCompat.ClipType.BaseType
                || makeClip.GetParameters()[0].ParameterType != optimizeLayer.GetParameters()[0].ParameterType) {
                throw new MissingMemberException(
                    "LayerToTreeService.Make0LengthClipForState(VFLayer, VFState) : VFMotion");
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
                makeClip,
                postfix: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(Make0LengthClipPostfix))
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
            droppedSideInLayer = false;
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
            droppedSideInLayer = false;
            return __exception;
        }

        // __0 is the layer being converted, __2 the binding→layers reverse index. Both stay
        // live only for the duration of this one OptimizeLayer call.
        private static void OptimizeLayerPrefix(object __0, object __2) {
            currentLayer = __0;
            layersByBinding = __2 as IDictionary;
            droppedSideInLayer = false;
            if (conflictingBindings == null) return;
            layersSeen++;
            LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";
        }

        private static Exception OptimizeLayerFinalizer(Exception __exception) {
            currentLayer = null;
            layersByBinding = null;
            droppedSideInLayer = false;
            return __exception;
        }

        /**
         * Drops a side whose zero-length clip only writes rest values that no other layer
         * writes. VRCFury replaces a null side with an empty clip, so that side writes
         * nothing and the bindings fall back to the rest values the clip held.
         *
         * Only one side per layer is dropped: with both sides gone VRCFury would find no
         * valid bindings and keep the layer. For the same reason a side is kept when another
         * state of the layer has no motion, and layers with a single state are left alone.
         */
        private static void Make0LengthClipPostfix(object __0, object __1, ref object __result) {
            if (conflictingBindings == null || droppedSideInLayer) return;
            if (currentLayer == null || layersByBinding == null) return;
            if (!ReferenceEquals(__0, currentLayer)) return;

            try {
                var clip = __result;
                if (clip == null) return;
                // A blendtree side is not a clip; leave those to stock conversion.
                if (!ClipCurveCompat.ClipType.IsInstanceOfType(clip)) return;
                if (!OtherStatesHaveMotions(currentLayer, __1)) return;
                if (!IsRestOnly(clip)) return;

                __result = null;
                droppedSideInLayer = true;
                upgraded++;
                LastUpgraded = upgraded;
                LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";
            } catch {
                // Leave the motion untouched; stock two-sided conversion proceeds.
            }
        }

        /**
         * True when the layer has at least one other state and every other state has a
         * motion. A state without a motion already becomes an empty side, so dropping this
         * side too would leave nothing to convert.
         */
        internal static bool OtherStatesHaveMotions(object layer, object state) {
            var machine = ToggleTreeCompat.LayerStateMachine.GetValue(layer);
            if (machine == null) return false;
            if (!(ToggleTreeCompat.SmStates.GetValue(machine) is IEnumerable states)) return false;
            var others = 0;
            foreach (var other in states) {
                if (other == null || ReferenceEquals(other, state)) continue;
                if (ToggleTreeCompat.StateMotion.GetValue(other) == null) return false;
                others++;
            }
            return others > 0;
        }

        private static bool IsRestOnly(object clip) {
            var avatarRoot = BuildPhaseHooks.CurrentAvatarRoot;
            if (avatarRoot == null) return false;
            var vfAvatarRoot = ClipCurveCompat.WrapGameObject(avatarRoot);
            if (vfAvatarRoot == null) return false;

            var curves = ClipCurveCompat.AllCurvesOf(clip);
            if (curves.Length == 0) return false;

            considered++;
            LastStats = $"candidateLayers={layersSeen} offSidesChecked={considered} oneSided={upgraded}";

            foreach (var entry in curves) {
                var binding = ClipCurveCompat.TupleBinding(entry);
                var curve = ClipCurveCompat.TupleCurve(entry);

                // AAPs and muscles have no resting value to fall back to.
                if (ClipCurveCompat.IsAnimatorBinding(binding)) return false;

                // VRCFury indexes its layers under the rotation-combined normal form.
                var normalized = ClipCurveCompat.Normalize(binding, true);
                if (conflictingBindings.Contains(normalized)) return false;

                // Stock two-sided conversion writes the rest value OVER lower-priority
                // layers; one-sided must not change that, so the candidate layer has to be
                // the only writer. The defaults layer is exempt: what it writes IS the rest
                // value, which is exactly what we substitute. A binding missing from the
                // index was filtered as invalid, so bail.
                if (!(layersByBinding[normalized] is IEnumerable writers)) return false;
                foreach (var writer in writers) {
                    if (ReferenceEquals(writer, currentLayer)) continue;
                    if (defaultLayer != null && ReferenceEquals(writer, defaultLayer)) continue;
                    return false;
                }

                if (curve == null || !ClipCurveCompat.IsFloat(curve)) return false;
                var floatCurve = ClipCurveCompat.FloatCurveOf(curve);
                if (floatCurve == null || !NoOpCurveStripPass.IsConstant(floatCurve, out var value)) return false;
                if (!ClipCurveCompat.TryGetRestValue(binding, vfAvatarRoot, out var rest)) return false;
                if (!NoOpCurveStripPass.ValuesMatch(
                        ClipCurveCompat.PropertyNameOf(binding), value, rest)) return false;
            }
            return true;
        }
    }
}
