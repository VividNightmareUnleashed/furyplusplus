using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Per-run snapshot + shared guards for the two toggle→blendtree conversion passes.
     * Both passes run right after FeatureOrder.LayerToTree, so every layer still present
     * is one VRCFury's own optimizer declined; we only convert the two closed-world
     * shapes ToggleBuilder emits that its optimizer cannot handle (3-state separateLocal,
     * 4-state fade). All matching happens on the raw AnimatorStateMachine graph.
     */
    internal static class ToggleConversionRuntime {
        internal sealed class Snapshot {
            internal object Fx;
            internal AnimatorController Raw;
            internal object LayerControl;
            internal object ValidateBindings;
            internal List<Entry> Layers;
        }

        internal sealed class Entry {
            internal object VfLayer;
            internal AnimatorStateMachine Machine;
            internal int Id;
            internal string Name;
            internal ICollection<EditorCurveBinding> Bindings;
            internal bool IsDefaultLayer;
            internal bool Converted;
        }

        /** Null when any live service is unavailable — callers skip the run. */
        internal static Snapshot Take() {
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var layerToTree = BuildPhaseHooks.GetService("VF.Service.LayerToTreeService");
            var layerControl = BuildPhaseHooks.GetService("VF.Service.AnimatorLayerControlOffsetService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            var validateBindings = BuildPhaseHooks.GetService("VF.Service.ValidateBindingsService");
            if (controllersService == null || layerToTree == null || layerControl == null
                || fixWd == null || validateBindings == null) {
                return null;
            }

            var fx = ToggleTreeCompat.GetFx.Invoke(controllersService, null);
            var raw = (AnimatorController)ToggleTreeCompat.GetRaw.Invoke(fx, null);
            var defaultLayer = ToggleTreeCompat.GetDefaultLayer.Invoke(fixWd, null);

            var snapshot = new Snapshot {
                Fx = fx,
                Raw = raw,
                LayerControl = layerControl,
                ValidateBindings = validateBindings,
                Layers = new List<Entry>()
            };
            foreach (var layer in ((IEnumerable)ToggleTreeCompat.GetLayers.Invoke(fx, null)).Cast<object>()) {
                snapshot.Layers.Add(new Entry {
                    VfLayer = layer,
                    Machine = VfLayerCompat.RootStateMachineField.GetValue(layer) as AnimatorStateMachine,
                    Id = (int)ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.LayerGetId, layer, null),
                    Name = (string)ToggleTreeCompat.LayerName.GetValue(layer),
                    Bindings = (ICollection<EditorCurveBinding>)ReflectionUtils.InvokeUnwrapped(
                        ToggleTreeCompat.GetBindingsAnimatedInLayer, layerToTree, new[] { layer }),
                    IsDefaultLayer = defaultLayer != null && defaultLayer.Equals(layer)
                });
            }
            snapshot.Layers.Sort((a, b) => a.Id.CompareTo(b.Id));
            return snapshot;
        }

        /**
         * Guards shared by both toggle shapes (mirrors LayerToTreeService.OptimizeLayer's
         * layer-level rejections). Returns false when the layer must not be converted.
         */
        internal static bool PassesCommonLayerGuards(Snapshot snapshot, Entry entry) {
            if (entry.IsDefaultLayer || entry.Machine == null) return false;
            if (!Mathf.Approximately((float)ToggleTreeCompat.LayerWeight.GetValue(entry.VfLayer), 1f)) return false;
            if ((AnimatorLayerBlendingMode)ToggleTreeCompat.LayerBlendingMode.GetValue(entry.VfLayer)
                == AnimatorLayerBlendingMode.Additive) return false;
            if ((bool)ReflectionUtils.InvokeUnwrapped(
                    ToggleTreeCompat.IsLayerTargeted, snapshot.LayerControl, new[] { entry.VfLayer })) return false;
            if (entry.Machine.stateMachines.Length != 0) return false;
            if (entry.Machine.anyStateTransitions.Length != 0) return false;
            if (entry.Machine.behaviours.Length != 0) return false;
            foreach (var child in entry.Machine.states) {
                var state = child.state;
                if (state == null) return false;
                if (state.behaviours.Length != 0) return false;
                if (state.timeParameterActive || state.speedParameterActive) return false;
            }
            // Rotations behave differently inside blend trees (mirror of stock guard).
            if (entry.Bindings.Any(binding => binding.propertyName == "rotation")) return false;
            return true;
        }

        /**
         * Mirror of stock LayerToTree's conflict guard: any still-existing higher-or-equal
         * priority layer animating one of our bindings blocks the conversion (the converted
         * content moves to the end of the stack, which must not steal their override).
         */
        internal static bool SharesBindingsWithHigherLayer(Snapshot snapshot, Entry entry) {
            return snapshot.Layers.Any(other =>
                other != entry
                && !other.Converted
                && other.Id >= entry.Id
                && other.Bindings.Any(binding => entry.Bindings.Contains(binding)));
        }

        /**
         * Stricter variant for fades: fractional blend weights write our bindings at
         * near-rest values instead of writing nothing, so ANY other layer animating the
         * same binding (either direction, defaults layer excluded) blocks conversion.
         */
        internal static bool SharesBindingsWithAnyLayer(Snapshot snapshot, Entry entry) {
            return snapshot.Layers.Any(other =>
                other != entry
                && !other.Converted
                && !other.IsDefaultLayer
                && other.Bindings.Any(binding => entry.Bindings.Contains(binding)));
        }

        internal static AnimatorControllerParameter FindParam(Snapshot snapshot, string name) {
            return snapshot.Raw.parameters.FirstOrDefault(parameter => parameter.name == name);
        }

        internal static bool ConditionsEqual(AnimatorCondition a, AnimatorCondition b) {
            if (a.parameter != b.parameter || a.mode != b.mode) return false;
            if (a.mode == AnimatorConditionMode.If || a.mode == AnimatorConditionMode.IfNot) return true;
            return a.threshold == b.threshold;
        }

        /** Negation as VFCondition.Not emits it for the modes we accept (If/IfNot, Equals/NotEqual). */
        internal static AnimatorCondition? Negate(AnimatorCondition condition) {
            switch (condition.mode) {
                case AnimatorConditionMode.If:
                    return new AnimatorCondition { parameter = condition.parameter, mode = AnimatorConditionMode.IfNot };
                case AnimatorConditionMode.IfNot:
                    return new AnimatorCondition { parameter = condition.parameter, mode = AnimatorConditionMode.If };
                case AnimatorConditionMode.Equals:
                    return new AnimatorCondition {
                        parameter = condition.parameter, mode = AnimatorConditionMode.NotEqual,
                        threshold = condition.threshold
                    };
                case AnimatorConditionMode.NotEqual:
                    return new AnimatorCondition {
                        parameter = condition.parameter, mode = AnimatorConditionMode.Equals,
                        threshold = condition.threshold
                    };
                default:
                    return null;
            }
        }

        internal static bool MotionHasValidBinding(Snapshot snapshot, Motion motion) {
            return motion != null && (bool)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.HasValidBinding, snapshot.ValidateBindings, new object[] { motion });
        }

        internal static bool MotionIsStatic(Motion motion) {
            return motion != null && (bool)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.MotionIsStatic, null, new object[] { motion });
        }

        internal static Motion LastFrameOrEmpty(Motion motion, string emptyName) {
            if (motion == null) return ToggleTreeCompat.NewEmptyClip(emptyName);
            return (Motion)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.MotionGetLastFrame, null, new object[] { motion, true });
        }

        /** True when every curve in the motion is a plain float curve (no material swaps, no AAPs). */
        internal static bool MotionIsPlainFloat(Motion motion) {
            var iterator = Activator.CreateInstance(ToggleTreeCompat.ClipsIteratorType);
            foreach (var clipObj in (IEnumerable)ToggleTreeCompat.ClipsFromMotion
                         .Invoke(iterator, new object[] { motion })) {
                if (!(clipObj is AnimationClip clip)) continue;
                foreach (EditorCurveBinding binding in (Array)ToggleTreeCompat.ClipGetAllBindings
                             .Invoke(null, new object[] { clip })) {
                    if (binding.isPPtrCurve) return false;
                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) return false;
                }
            }
            return true;
        }
    }
}
