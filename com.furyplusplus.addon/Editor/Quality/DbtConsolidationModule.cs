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
    internal sealed class DbtConsolidationModule : Module {
        internal static DbtConsolidationModule Instance { get; private set; }

        internal DbtConsolidationModule() {
            Instance = this;
        }

        internal override string Id => "dbtConsolidation";
        internal override string DisplayName => "Consolidate blendtree layers";
        internal override ModuleKind Kind => ModuleKind.Quality;
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
    }

    internal static class DbtConsolidationPass {
        internal static string LastStats;

        private static MethodInfo getFx;
        private static MethodInfo getLayers;
        private static MethodInfo getBindingsAnimatedInLayer;
        private static MethodInfo getDefaultLayer;
        private static MethodInfo isLayerTargeted;
        private static MethodInfo layerRemove;
        private static PropertyInfo layerWeight;
        private static PropertyInfo layerMask;
        private static PropertyInfo layerName;
        private static Type clipsIteratorType;
        private static MethodInfo clipsFrom;
        private static MethodInfo getAllBindings;

        internal static void Resolve() {
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            var layerToTreeType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.LayerToTreeService"), "VF.Service.LayerToTreeService");
            var layerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFLayer"), "VF.Utils.Controller.VFLayer");
            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"), "VF.Utils.Controller.VFController");
            var layerControlType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.AnimatorLayerControlOffsetService"),
                "VF.Service.AnimatorLayerControlOffsetService");
            var clipExtType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions"), "VF.Utils.AnimationClipExtensions");

            VfLayerCompat.EnsureResolved();

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            getFx = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetFx",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetFx()");
            getLayers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                    method => method.GetParameters().Length == 0),
                "VFController.GetLayers()");
            getBindingsAnimatedInLayer = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(layerToTreeType, "GetBindingsAnimatedInLayer",
                    method => method.GetParameters().Length == 1),
                "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            getDefaultLayer = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(
                    ReflectionUtils.FindType("VF.Service.FixWriteDefaultsService"),
                    "GetDefaultLayer",
                    method => method.GetParameters().Length == 0),
                "FixWriteDefaultsService.GetDefaultLayer()");
            isLayerTargeted = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(layerControlType, "IsLayerTargeted",
                    method => method.GetParameters().Length == 1),
                "AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)");
            layerRemove = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(layerType, "Remove"), "VFLayer.Remove()");
            layerWeight = ReflectionUtils.Demand(layerType.GetProperty("weight", any), "VFLayer.weight");
            layerMask = ReflectionUtils.Demand(layerType.GetProperty("mask", any), "VFLayer.mask");
            layerName = ReflectionUtils.Demand(layerType.GetProperty("name", any), "VFLayer.name");

            clipsIteratorType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips"), "VF.Utils.AnimatorIterator+Clips");
            clipsFrom = ReflectionUtils.Demand(
                clipsIteratorType.GetMethods(any)
                    .SingleOrDefault(method => method.Name == "From"
                                               && method.GetParameters().Length == 1
                                               && method.GetParameters()[0].ParameterType == vfControllerType),
                "AnimatorIterator.Clips.From(VFController)");
            getAllBindings = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "GetAllBindings",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.GetAllBindings(clip)");
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
                var iterator = Activator.CreateInstance(clipsIteratorType);
                foreach (var clipObj in (IEnumerable)clipsFrom.Invoke(iterator, new[] { fx })) {
                    if (!(clipObj is AnimationClip clip)) continue;
                    foreach (EditorCurveBinding binding in (Array)getAllBindings.Invoke(null, new object[] { clip })) {
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
