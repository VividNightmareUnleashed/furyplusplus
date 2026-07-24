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
     * Merges the separate single-state Direct-BlendTree FX layers that VRCFury's services
     * each create for themselves (layer-to-tree, object-enable markers, physbone reset,
     * force-state trees, …) into one layer — every animator layer has a fixed per-frame
     * cost for everyone rendering the avatar.
     *
     * Runs right after FeatureOrder.LayerToTree (before AnimatorLayerControlFix finalizes
     * behaviour layer indices). Strictly conservative merge rules:
     *  - shape: exactly one state (the default), motion a Direct tree, weight 1, no mask,
     *    no behaviours, no transitions, not targeted by an AnimatorLayerControl, not the
     *    defaults layer, no normalized blend values;
     *  - pairwise-DISJOINT write bindings (inside one direct tree overlapping writes SUM,
     *    across layers they override — merging overlaps would change output);
     *  - no AAP involvement in either direction: candidates neither read params that any
     *    FX clip AAP-writes nor AAP-write anything themselves, so moving content earlier
     *    in evaluation order cannot change same-frame dataflow.
     */
    internal sealed class DbtConsolidationModule : Module<DbtConsolidationModule> {

        internal override string Id => "dbtConsolidation";
        internal override string DisplayName => "Consolidate blendtree layers";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Animator layers";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Merges VRCFury's separate single-state direct-blendtree FX layers into one, " +
            "cutting per-frame animator layer overhead.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            DbtConsolidationPass.Resolve();
            BuildPhaseHooks.RegisterAfter("LayerToTree", Id, _ => DbtConsolidationPass.Run());
        }

        internal override string ReportStats() {
            return DbtConsolidationPass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return DbtConsolidationPass.LastMergedLayers > 0
                ? ($"-{DbtConsolidationPass.LastMergedLayers} layers last bake", DbtConsolidationPass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class DbtConsolidationPass {
        internal static string LastStats;
        internal static int LastMergedLayers;

        private static MethodInfo getFx;
        private static MethodInfo getLayers;
        private static MethodInfo getBindingsAnimatedInLayer;
        private static MethodInfo getDefaultLayer;
        private static MethodInfo isLayerTargeted;
        private static MethodInfo layerRemove;
        private static PropertyInfo layerWeight;
        private static PropertyInfo layerMask;
        private static PropertyInfo layerName;

        internal static void Resolve() {
            var layerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFLayer"), "VF.Utils.Controller.VFLayer");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.LayerStateMachine, "VFLayer.stateMachine");
            ReflectionUtils.Demand(ToggleTreeCompat.LayerHasSubMachines, "VFLayer.hasSubMachines");
            ReflectionUtils.Demand(ToggleTreeCompat.SmStates, "VFStateMachine.states");
            ReflectionUtils.Demand(ToggleTreeCompat.SmDefaultState, "VFStateMachine.defaultState");
            ReflectionUtils.Demand(ToggleTreeCompat.SmEntryTransitions, "VFStateMachine.entryTransitions");
            ReflectionUtils.Demand(ToggleTreeCompat.SmAnyStateTransitions, "VFStateMachine.anyStateTransitions");
            ReflectionUtils.Demand(ToggleTreeCompat.SmBehaviours, "VFStateMachine.behaviours");
            ReflectionUtils.Demand(ToggleTreeCompat.StateMotion, "VFState.motion");
            ReflectionUtils.Demand(ToggleTreeCompat.StateTransitions, "VFState.transitions");
            ReflectionUtils.Demand(ToggleTreeCompat.StateBehaviours, "VFState.behaviours");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeType, "VF.Utils.Controller.VFTree");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeChildren, "VFTree.children");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeAddChild, "VFTree.AddChild(child)");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeBlendType, "VFTree.blendType");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeNormalizedBlendValues, "VFTree.NormalizedBlendValues");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeBlendParameter, "VFTree.BlendParameter");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeBlendParameterY, "VFTree.BlendParameterY");
            ReflectionUtils.Demand(ToggleTreeCompat.ChildMotion, "VFTreeChild.motion");
            ReflectionUtils.Demand(ToggleTreeCompat.ChildDirectBlendParameter, "VFTreeChild.directBlendParameter");
            getFx = ReflectionUtils.Demand(ToggleTreeCompat.GetFx, "ControllersService.GetFx()");
            getLayers = ReflectionUtils.Demand(ToggleTreeCompat.GetLayers, "VFController.GetLayers()");
            getBindingsAnimatedInLayer = ReflectionUtils.Demand(
                ToggleTreeCompat.GetBindingsAnimatedInLayer,
                "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            getDefaultLayer = ReflectionUtils.Demand(
                ToggleTreeCompat.GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");
            isLayerTargeted = ReflectionUtils.Demand(
                ToggleTreeCompat.IsLayerTargeted, "AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)");
            layerRemove = ReflectionUtils.Demand(ToggleTreeCompat.LayerRemove, "VFLayer.Remove()");
            layerWeight = ReflectionUtils.Demand(ToggleTreeCompat.LayerWeight, "VFLayer.weight");
            layerName = ReflectionUtils.Demand(ToggleTreeCompat.LayerName, "VFLayer.name");
            // mask is this pass's own extra member — the area holder carries the shared set.
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            layerMask = ReflectionUtils.Demand(layerType.GetProperty("mask", any), "VFLayer.mask");

            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipGetAllBindings, "VFClip.GetAllBindings()");
        }

        internal static void Run() {
            if (DbtConsolidationModule.Instance?.Enabled != true) return;
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var layerToTree = BuildPhaseHooks.GetService("VF.Service.LayerToTreeService");
            var layerControl = BuildPhaseHooks.GetService("VF.Service.AnimatorLayerControlOffsetService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            if (controllersService == null || layerToTree == null || layerControl == null || fixWd == null) return;

            try {
                var fx = getFx.Invoke(controllersService, null);
                var defaultLayer = getDefaultLayer.Invoke(fixWd, null);

                // Params AAP-written by any FX clip: candidates must not touch these at all.
                var aapWritten = new HashSet<string>();
                foreach (var clip in ClipCurveCompat.ClipsFrom(fx)) {
                    if (clip == null) continue;
                    foreach (var binding in ClipCurveCompat.AllBindingsOf(clip)) {
                        // Animator-stream bindings: AAPs, plus humanoid muscles whose names
                        // can never collide with a parameter — blocking on those too only
                        // makes the candidate guard stricter.
                        if (ClipCurveCompat.IsAnimatorBinding(binding)) {
                            aapWritten.Add(ClipCurveCompat.PropertyNameOf(binding));
                        }
                    }
                }

                var candidates = new List<(object Layer, object Tree, HashSet<object> Bindings)>();
                foreach (var layer in ((IEnumerable)getLayers.Invoke(fx, null)).Cast<object>()) {
                    if (defaultLayer != null && ReferenceEquals(defaultLayer, layer)) continue;

                    var machine = ToggleTreeCompat.LayerStateMachine.GetValue(layer);
                    if (machine == null) continue;
                    if ((bool)ToggleTreeCompat.LayerHasSubMachines.GetValue(layer)) continue;
                    var states = ToggleConversionRuntime.StatesOf(machine);
                    if (states.Count != 1) continue;
                    if (ToggleConversionRuntime.CountOf(
                            ToggleTreeCompat.SmAnyStateTransitions.GetValue(machine)) != 0) continue;
                    if (ToggleConversionRuntime.CountOf(
                            ToggleTreeCompat.SmEntryTransitions.GetValue(machine)) != 0) continue;
                    if (ToggleConversionRuntime.CountOf(
                            ToggleTreeCompat.SmBehaviours.GetValue(machine)) != 0) continue;
                    var state = states[0];
                    if (state == null) continue;
                    if (!ReferenceEquals(ToggleTreeCompat.SmDefaultState.GetValue(machine), state)) continue;
                    if (ToggleConversionRuntime.TransitionsOf(state).Count != 0) continue;
                    if (ToggleConversionRuntime.CountOf(
                            ToggleTreeCompat.StateBehaviours.GetValue(state)) != 0) continue;
                    var tree = ToggleConversionRuntime.MotionOf(state);
                    if (tree == null || !ToggleTreeCompat.TreeType.IsInstanceOfType(tree)) continue;
                    if ((BlendTreeType)ToggleTreeCompat.TreeBlendType.GetValue(tree)
                        != BlendTreeType.Direct) continue;
                    if (!Mathf.Approximately((float)layerWeight.GetValue(layer), 1f)) continue;
                    if (layerMask.GetValue(layer) != null) continue;
                    if ((bool)ReflectionUtils.InvokeUnwrapped(isLayerTargeted, layerControl, new[] { layer })) continue;
                    // The detached model exposes this directly — no SerializedObject probe needed.
                    if ((bool)ToggleTreeCompat.TreeNormalizedBlendValues.GetValue(tree)) continue;

                    // AAP hygiene in both directions.
                    if (TreeTouchesAaps(tree, aapWritten)) continue;

                    var bindings = new HashSet<object>();
                    var writesAaps = false;
                    foreach (var binding in (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                                 getBindingsAnimatedInLayer, layerToTree, new[] { layer })) {
                        if (ClipCurveCompat.IsAnimatorBinding(binding)) { writesAaps = true; break; }
                        bindings.Add(binding);
                    }
                    if (writesAaps) continue;
                    candidates.Add((layer, tree, bindings));
                }

                if (candidates.Count < 2) {
                    LastStats = $"mergedLayers=0 (candidates={candidates.Count})";
                    LastMergedLayers = 0;
                    return;
                }

                // Greedy grouping in layer order with pairwise-disjoint write sets.
                var target = candidates[0];
                var targetBindings = new HashSet<object>(target.Bindings);
                var merged = new List<object>();
                foreach (var donor in candidates.Skip(1)) {
                    if (donor.Bindings.Overlaps(targetBindings)) continue;
                    // VFTree.children is read-only now; append through AddChild instead of
                    // rebuilding the array.
                    foreach (var child in (IEnumerable)ToggleTreeCompat.TreeChildren.GetValue(donor.Tree)) {
                        ReflectionUtils.InvokeUnwrapped(
                            ToggleTreeCompat.TreeAddChild, target.Tree, new[] { child });
                    }
                    foreach (var binding in donor.Bindings) targetBindings.Add(binding);
                    merged.Add(donor.Layer);
                }
                foreach (var donor in merged) {
                    ReflectionUtils.InvokeUnwrapped(layerRemove, donor, null);
                }

                LastMergedLayers = merged.Count;
                LastStats = $"mergedLayers={merged.Count} (candidates={candidates.Count})";
                if (merged.Count > 0) {
                    Log.Info($"Consolidated {merged.Count + 1} direct-blendtree layers into one " +
                             $"(\"{layerName.GetValue(target.Layer)}\").");
                }
            } catch (Exception e) {
                Log.Warn("DBT consolidation skipped: " + e.Message);
            }
        }

        /** Walks a VFTree (and any nested VFTrees) looking for a blend parameter an FX clip AAP-writes. */
        private static bool TreeTouchesAaps(object tree, HashSet<string> aapWritten) {
            var stack = new Stack<object>();
            var seen = new HashSet<object>();
            stack.Push(tree);
            while (stack.Count > 0) {
                var current = stack.Pop();
                if (!seen.Add(current)) continue;
                var isDirect = (BlendTreeType)ToggleTreeCompat.TreeBlendType.GetValue(current)
                               == BlendTreeType.Direct;
                if (!isDirect) {
                    if (aapWritten.Contains(
                            (string)ToggleTreeCompat.TreeBlendParameter.GetValue(current))) return true;
                    if ((BlendTreeType)ToggleTreeCompat.TreeBlendType.GetValue(current)
                        != BlendTreeType.Simple1D
                        && aapWritten.Contains(
                            (string)ToggleTreeCompat.TreeBlendParameterY.GetValue(current))) {
                        return true;
                    }
                }
                foreach (var child in (IEnumerable)ToggleTreeCompat.TreeChildren.GetValue(current)) {
                    if (isDirect) {
                        var directParam = (string)ToggleTreeCompat.ChildDirectBlendParameter.GetValue(child);
                        if (!string.IsNullOrEmpty(directParam) && aapWritten.Contains(directParam)) {
                            return true;
                        }
                    }
                    var childMotion = ToggleTreeCompat.ChildMotion.GetValue(child);
                    if (childMotion != null && ToggleTreeCompat.TreeType.IsInstanceOfType(childMotion)) {
                        stack.Push(childMotion);
                    }
                }
            }
            return false;
        }
    }
}
