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

            VfLayerCompat.EnsureResolved();
            ReflectionUtils.Demand(VfLayerCompat.RootStateMachineField, "VFLayer.rootStateMachine");

            ToggleTreeCompat.EnsureResolved();
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
            ReflectionUtils.Demand(ClipCurveCompat.ClipsFromController, "AnimatorIterator.Clips.From(VFController)");
            ReflectionUtils.Demand(ClipCurveCompat.GetAllBindings, "AnimationClipExtensions.GetAllBindings(clip)");
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
                foreach (var clipObj in ClipCurveCompat.ClipsFrom(fx)) {
                    if (!(clipObj is AnimationClip clip)) continue;
                    foreach (EditorCurveBinding binding in
                             (Array)ClipCurveCompat.GetAllBindings.Invoke(null, new object[] { clip })) {
                        if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) {
                            aapWritten.Add(binding.propertyName);
                        }
                    }
                }

                var candidates = new List<(object Layer, BlendTree Tree, ICollection<EditorCurveBinding> Bindings)>();
                foreach (var layer in ((IEnumerable)getLayers.Invoke(fx, null)).Cast<object>()) {
                    if (defaultLayer != null && defaultLayer.Equals(layer)) continue;

                    var machine = VfLayerCompat.RootStateMachineField.GetValue(layer) as AnimatorStateMachine;
                    if (machine == null) continue;
                    if (machine.states.Length != 1 || machine.stateMachines.Length != 0) continue;
                    if (machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0) continue;
                    if (machine.behaviours.Length != 0) continue;
                    var state = machine.states[0].state;
                    if (state == null || machine.defaultState != state) continue;
                    if (state.transitions.Length != 0 || state.behaviours.Length != 0) continue;
                    if (!(state.motion is BlendTree tree) || tree.blendType != BlendTreeType.Direct) continue;
                    if (!Mathf.Approximately((float)layerWeight.GetValue(layer), 1f)) continue;
                    if (layerMask.GetValue(layer) != null) continue;
                    if ((bool)ReflectionUtils.InvokeUnwrapped(isLayerTargeted, layerControl, new[] { layer })) continue;
                    if (HasNormalizedBlendValues(tree)) continue;

                    // AAP hygiene in both directions.
                    if (TreeTouchesAaps(tree, aapWritten)) continue;

                    var bindings = (ICollection<EditorCurveBinding>)ReflectionUtils.InvokeUnwrapped(
                        getBindingsAnimatedInLayer, layerToTree, new[] { layer });
                    if (bindings.Any(binding => binding.type == typeof(Animator)
                                                && string.IsNullOrEmpty(binding.path))) {
                        continue; // writes AAPs itself
                    }
                    candidates.Add((layer, tree, bindings));
                }

                if (candidates.Count < 2) {
                    LastStats = null;
                    LastMergedLayers = 0;
                    return;
                }

                // Greedy grouping in layer order with pairwise-disjoint write sets.
                var target = candidates[0];
                var targetBindings = new HashSet<EditorCurveBinding>(target.Bindings);
                var merged = new List<object>();
                foreach (var donor in candidates.Skip(1)) {
                    if (donor.Bindings.Any(targetBindings.Contains)) continue;
                    var children = target.Tree.children
                        .Concat(donor.Tree.children)
                        .ToArray();
                    target.Tree.children = children;
                    foreach (var binding in donor.Bindings) targetBindings.Add(binding);
                    merged.Add(donor.Layer);
                }
                foreach (var donor in merged) {
                    ReflectionUtils.InvokeUnwrapped(layerRemove, donor, null);
                }

                LastMergedLayers = merged.Count;
                if (merged.Count > 0) {
                    Log.Info($"Consolidated {merged.Count + 1} direct-blendtree layers into one " +
                             $"(\"{layerName.GetValue(target.Layer)}\").");
                    LastStats = $"mergedLayers={merged.Count}";
                } else {
                    LastStats = null;
                }
            } catch (Exception e) {
                Log.Warn("DBT consolidation skipped: " + e.Message);
            }
        }

        private static bool TreeTouchesAaps(BlendTree tree, HashSet<string> aapWritten) {
            var stack = new Stack<BlendTree>();
            var seen = new HashSet<BlendTree>();
            stack.Push(tree);
            while (stack.Count > 0) {
                var current = stack.Pop();
                if (!seen.Add(current)) continue;
                if (current.blendType == BlendTreeType.Direct) {
                    foreach (var child in current.children) {
                        if (!string.IsNullOrEmpty(child.directBlendParameter)
                            && aapWritten.Contains(child.directBlendParameter)) {
                            return true;
                        }
                        if (child.motion is BlendTree childTree) stack.Push(childTree);
                    }
                } else {
                    if (aapWritten.Contains(current.blendParameter)) return true;
                    if (current.blendType != BlendTreeType.Simple1D
                        && aapWritten.Contains(current.blendParameterY)) {
                        return true;
                    }
                    foreach (var child in current.children) {
                        if (child.motion is BlendTree childTree) stack.Push(childTree);
                    }
                }
            }
            return false;
        }

        private static bool HasNormalizedBlendValues(BlendTree tree) {
            using (var serialized = new SerializedObject(tree)) {
                var property = serialized.FindProperty("m_NormalizedBlendValues");
                return property != null && property.boolValue;
            }
        }
    }
}
