using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor;
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
     * ValuesMatch — one doctrine for both modules. Requires the layer-to-tree binding
     * index module (it publishes which candidate layer is being converted); silently
     * inactive without it.
     */
    internal sealed class OffSideEliminationModule : Module<OffSideEliminationModule> {
        internal override string Id => "offSideElimination";
        internal override string DisplayName => "One-sided blendtree toggles";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Animator layers";
        internal override string Description =>
            "Converts blendtree toggles whose off state only writes resting values to the " +
            "one-sided form — no per-frame writes while the toggle is off. Requires the " +
            "layer-to-tree binding index module.";

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

        private static System.Reflection.MethodInfo getEmptyClip;

        // Rebuilt at the LayerToTree boundary each run; null outside it.
        [ThreadStatic] private static HashSet<EditorCurveBinding> conflictingBindings;
        [ThreadStatic] private static Dictionary<EditorCurveBinding, int> fxWriterLayerCount;
        [ThreadStatic] private static AnimationClip emptyClip;
        [ThreadStatic] private static int upgraded;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var serviceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.LayerToTreeService"), "VF.Service.LayerToTreeService");
            var clipFactoryType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ClipFactoryService"), "VF.Service.ClipFactoryService");

            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipsFromController, "AnimatorIterator.Clips.From(VFController)");
            ReflectionUtils.Demand(ClipCurveCompat.GetAllBindings, "AnimationClipExtensions.GetAllBindings(clip)");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(ToggleTreeCompat.GetLayers, "VFController.GetLayers()");
            ReflectionUtils.Demand(ToggleTreeCompat.GetBindingsAnimatedInLayer,
                "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            ReflectionUtils.Demand(ToggleTreeCompat.GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");

            getEmptyClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipFactoryType, "GetEmptyClip",
                    method => method.GetParameters().Length == 0),
                "ClipFactoryService.GetEmptyClip()");

            var optimize = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(serviceType, "Optimize",
                    method => method.GetParameters().Length == 4),
                "LayerToTreeService.Optimize(condition, on, off, tree)");
            if (!typeof(Motion).IsAssignableFrom(optimize.GetParameters()[2].ParameterType)) {
                throw new InvalidOperationException("target signature mismatch");
            }

            BuildPhaseHooks.RegisterBefore("LayerToTree", OffSideEliminationModule.Instance.Id,
                _ => PrepareForRun());

            harmony.Patch(
                optimize,
                prefix: new HarmonyMethod(typeof(OffSideEliminationPatch), nameof(OptimizePrefix))
            );
        }

        /** Builds the cross-layer conflict data once per build, just before LayerToTree runs. */
        private static void PrepareForRun() {
            conflictingBindings = null;
            fxWriterLayerCount = null;
            emptyClip = null;
            upgraded = 0;
            if (OffSideEliminationModule.Instance?.Enabled != true) return;

            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var clipFactory = BuildPhaseHooks.GetService("VF.Service.ClipFactoryService");
            var layerToTree = BuildPhaseHooks.GetService("VF.Service.LayerToTreeService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            if (controllersService == null || clipFactory == null || layerToTree == null || fixWd == null) return;

            try {
                // Bindings animated by NON-FX controllers always conflict (they must keep
                // their own timing/override semantics).
                var conflicts = new HashSet<EditorCurveBinding>();
                var fx = ToggleTreeCompat.GetFx.Invoke(controllersService, null);
                foreach (var manager in (IEnumerable)ClipCurveCompat.GetAllUsedControllers
                             .Invoke(controllersService, null)) {
                    if (ReferenceEquals(manager, fx)) continue;
                    foreach (var clipObj in ClipCurveCompat.ClipsFrom(manager)) {
                        if (!(clipObj is AnimationClip clip)) continue;
                        foreach (EditorCurveBinding binding in
                                 (Array)ClipCurveCompat.GetAllBindings.Invoke(null, new object[] { clip })) {
                            conflicts.Add(binding);
                        }
                    }
                }

                // How many FX layers write each binding — the defaults layer excluded (its
                // writes are rest values by construction, which is exactly what one-sided
                // conversion substitutes). A binding is one-sided-safe only when its sole
                // FX writer is the candidate layer itself: stock two-sided conversion
                // writes rest OVER lower-priority layers while off; one-sided must not
                // change that, so any other writer keeps the two-sided form. Counts are
                // computed pre-pass and only ever over-count as layers convert — safe.
                var writerCount = new Dictionary<EditorCurveBinding, int>();
                var defaultLayer = ToggleTreeCompat.GetDefaultLayer.Invoke(fixWd, null);
                foreach (var layer in (IEnumerable)ToggleTreeCompat.GetLayers.Invoke(fx, null)) {
                    if (defaultLayer != null && defaultLayer.Equals(layer)) continue;
                    var bindings = (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                        ToggleTreeCompat.GetBindingsAnimatedInLayer, layerToTree, new[] { layer });
                    foreach (EditorCurveBinding binding in bindings) {
                        writerCount.TryGetValue(binding, out var count);
                        writerCount[binding] = count + 1;
                    }
                }

                conflictingBindings = conflicts;
                fxWriterLayerCount = writerCount;
                emptyClip = getEmptyClip.Invoke(clipFactory, null) as AnimationClip;
            } catch (Exception e) {
                conflictingBindings = null;
                fxWriterLayerCount = null;
                emptyClip = null;
                Log.Warn("Off-side elimination fell back to VRCFury: " + e.Message);
            }
        }

        // __2 is the off-side motion; replacing it with an empty clip routes VRCFury's own
        // code down its existing one-sided branch. The ThreadStatic context doubles as the
        // enabled signal — PrepareForRun leaves it null when the module is off.
        private static void OptimizePrefix(ref Motion __2) {
            if (conflictingBindings == null || emptyClip == null) return;
            var candidateBindings = LayerToTreeBindingIndexPatch.CurrentCandidateBindings;
            if (candidateBindings == null) return; // stock loop running — no layer context

            try {
                if (!(__2 is AnimationClip offClip) || offClip == emptyClip) return;
                var avatarRoot = BuildPhaseHooks.CurrentAvatarRoot;
                if (avatarRoot == null) return;

                var curves = ClipCurveCompat.AllCurvesOf(offClip);
                if (curves.Length == 0) return;

                foreach (var entry in curves) {
                    var binding = ClipCurveCompat.TupleBinding(entry);
                    var curve = ClipCurveCompat.TupleCurve(entry);

                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) return;
                    if (conflictingBindings.Contains(binding)) return;
                    // The candidate's own bindings (its on clip) are the only acceptable
                    // writers; any other FX layer writing this binding → keep two-sided.
                    if (!candidateBindings.Contains(binding)) return;
                    if (!fxWriterLayerCount.TryGetValue(binding, out var writers) || writers != 1) return;

                    if (curve == null || !ClipCurveCompat.IsFloat(curve)) return;
                    var floatCurve = ClipCurveCompat.FloatCurveOf(curve);
                    if (floatCurve == null || !NoOpCurveStripPass.IsConstant(floatCurve, out var value)) return;
                    if (!AnimationUtility.GetFloatValue(avatarRoot, binding, out var rest)) return;
                    if (!NoOpCurveStripPass.ValuesMatch(binding, value, rest)) return;
                }

                __2 = emptyClip;
                upgraded++;
                LastUpgraded = upgraded;
                LastStats = $"oneSided={upgraded}";
            } catch {
                // Leave the motion untouched — stock two-sided conversion proceeds.
            }
        }
    }
}
