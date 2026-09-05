using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Per-run snapshot + shared guards for the two toggle→blendtree conversion passes.
     * Both passes run right after FeatureOrder.LayerToTree, so every layer still present
     * is one VRCFury's own optimizer declined; we only convert the two closed-world
     * shapes ToggleBuilder emits that its optimizer cannot handle (3-state separateLocal,
     * 4-state fade).
     *
     * As of VRCFury 1.1382 there is no AnimatorStateMachine to match against — matching
     * walks the detached VFStateMachine/VFState/VFTransition graph instead. Conditions are
     * still AnimatorCondition, so all of the condition algebra below is unchanged.
     */
    internal static class ToggleConversionRuntime {
        internal sealed class Snapshot {
            internal object Fx;
            internal object LayerControl;
            internal object ValidateBindings;
            internal object BindingRoot;
            internal List<Entry> Layers;
        }

        internal sealed class Entry {
            internal object VfLayer;
            /** The layer's root VFStateMachine, or null when it has none. */
            internal object StateMachine;
            internal bool HasSubMachines;
            internal int Id;
            internal string Name;
            /** Boxed VFBindings — value-equal across layers, so a HashSet compares correctly. */
            internal HashSet<object> Bindings;
            internal bool IsDefaultLayer;
            internal bool Converted;
        }

        /**
         * How the last snapshot went, for module stats. "0 converted" on its own cannot tell
         * a healthy no-match run apart from a broken one, which is exactly how a resolution
         * bug hid in OffSideEliminationPatch — so every pass reports this alongside its count.
         */
        internal static string LastSnapshotSummary;

        /** Null when any live service is unavailable — callers skip the run. */
        internal static Snapshot Take() {
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var layerToTree = BuildPhaseHooks.GetService("VF.Service.LayerToTreeService");
            var layerControl = BuildPhaseHooks.GetService("VF.Service.AnimatorLayerControlOffsetService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            var validateBindings = BuildPhaseHooks.GetService("VF.Service.ValidateBindingsService");
            if (controllersService == null || layerToTree == null || layerControl == null
                || fixWd == null || validateBindings == null) {
                LastSnapshotSummary = "no services: "
                                      + (controllersService == null ? "controllers " : "")
                                      + (layerToTree == null ? "layerToTree " : "")
                                      + (layerControl == null ? "layerControl " : "")
                                      + (fixWd == null ? "fixWd " : "")
                                      + (validateBindings == null ? "validateBindings" : "");
                return null;
            }

            var fx = ToggleTreeCompat.GetFx.Invoke(controllersService, null);
            var defaultLayer = ToggleTreeCompat.GetDefaultLayer.Invoke(fixWd, null);

            var snapshot = new Snapshot {
                Fx = fx,
                LayerControl = layerControl,
                ValidateBindings = validateBindings,
                BindingRoot = ToggleTreeCompat.GetBindingRoot(layerToTree),
                Layers = new List<Entry>()
            };
            foreach (var layer in ((IEnumerable)ToggleTreeCompat.GetLayers.Invoke(fx, null)).Cast<object>()) {
                var bindings = new HashSet<object>();
                foreach (var binding in (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                             ToggleTreeCompat.GetBindingsAnimatedInLayer, layerToTree, new[] { layer })) {
                    bindings.Add(binding);
                }
                snapshot.Layers.Add(new Entry {
                    VfLayer = layer,
                    StateMachine = ToggleTreeCompat.LayerStateMachine.GetValue(layer),
                    HasSubMachines = (bool)ToggleTreeCompat.LayerHasSubMachines.GetValue(layer),
                    Id = (int)ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.LayerGetId, layer, null),
                    Name = (string)ToggleTreeCompat.LayerName.GetValue(layer),
                    Bindings = bindings,
                    IsDefaultLayer = defaultLayer != null && ReferenceEquals(defaultLayer, layer)
                });
            }
            snapshot.Layers.Sort((a, b) => a.Id.CompareTo(b.Id));
            var withStates = snapshot.Layers.Count(entry => entry.StateMachine != null);
            var passedGuards = snapshot.Layers.Count(entry => PassesCommonLayerGuards(snapshot, entry));
            LastSnapshotSummary =
                $"layers={snapshot.Layers.Count} withStateMachine={withStates} passedGuards={passedGuards}";
            return snapshot;
        }

        // ---- VF graph accessors ----

        /** The states of a VFStateMachine, in declaration order. */
        internal static List<object> StatesOf(object stateMachine) {
            var output = new List<object>();
            if (stateMachine == null) return output;
            foreach (var state in (IEnumerable)ToggleTreeCompat.SmStates.GetValue(stateMachine)) {
                output.Add(state);
            }
            return output;
        }

        internal static List<object> TransitionsOf(object state) {
            var output = new List<object>();
            if (state == null) return output;
            foreach (var transition in (IEnumerable)ToggleTreeCompat.StateTransitions.GetValue(state)) {
                output.Add(transition);
            }
            return output;
        }

        internal static int CountOf(object collection) {
            switch (collection) {
                case null: return 0;
                case ICollection typed: return typed.Count;
                default: {
                    var count = 0;
                    foreach (var unused in (IEnumerable)collection) count++;
                    return count;
                }
            }
        }

        internal static object MotionOf(object state) {
            return state == null ? null : ToggleTreeCompat.StateMotion.GetValue(state);
        }

        internal static AnimatorCondition[] ConditionsOf(object transition) {
            return ToggleTreeCompat.TrConditions.GetValue(transition) as AnimatorCondition[]
                   ?? Array.Empty<AnimatorCondition>();
        }

        internal static object DestinationOf(object transition) {
            return ToggleTreeCompat.TrDestinationState.GetValue(transition);
        }

        internal static bool IsExit(object transition) {
            return (bool)ToggleTreeCompat.TrIsExit.GetValue(transition);
        }

        internal static bool HasExitTime(object transition) {
            return (bool)ToggleTreeCompat.TrHasExitTime.GetValue(transition);
        }

        internal static float DurationOf(object transition) {
            return (float)ToggleTreeCompat.TrDuration.GetValue(transition);
        }

        /**
         * Guards shared by both toggle shapes (mirrors LayerToTreeService.OptimizeLayer's
         * layer-level rejections). Returns false when the layer must not be converted.
         */
        internal static bool PassesCommonLayerGuards(Snapshot snapshot, Entry entry) {
            if (entry.IsDefaultLayer || entry.StateMachine == null) return false;
            if (ToggleTreeCompat.LayerMask.GetValue(entry.VfLayer) != null) return false;
            if (!Mathf.Approximately((float)ToggleTreeCompat.LayerWeight.GetValue(entry.VfLayer), 1f)) return false;
            if ((AnimatorLayerBlendingMode)ToggleTreeCompat.LayerBlendingMode.GetValue(entry.VfLayer)
                == AnimatorLayerBlendingMode.Additive) return false;
            if ((bool)ReflectionUtils.InvokeUnwrapped(
                    ToggleTreeCompat.IsLayerTargeted, snapshot.LayerControl, new[] { entry.VfLayer })) return false;
            if (entry.HasSubMachines) return false;
            if (CountOf(ToggleTreeCompat.SmAnyStateTransitions.GetValue(entry.StateMachine)) != 0) return false;
            if (CountOf(ToggleTreeCompat.SmBehaviours.GetValue(entry.StateMachine)) != 0) return false;
            foreach (var state in StatesOf(entry.StateMachine)) {
                if (state == null) return false;
                if (CountOf(ToggleTreeCompat.StateBehaviours.GetValue(state)) != 0) return false;
                if (!HasDefaultPlayback(state)) return false;
            }
            // Rotations behave differently inside blend trees (mirror of stock guard). The
            // per-layer index is already normalized with combineRotation, so every rotation
            // spelling collapses to this one property name.
            foreach (var binding in entry.Bindings) {
                if (ClipCurveCompat.PropertyNameOf(binding) == "rotation") return false;
            }
            return true;
        }

        internal static bool HasDefaultPlayback(object state) {
            return (float)ToggleTreeCompat.StateSpeed.GetValue(state) == 1f
                && !(bool)ToggleTreeCompat.StateTimeParamActive.GetValue(state)
                && !(bool)ToggleTreeCompat.StateSpeedParamActive.GetValue(state)
                && !(bool)ToggleTreeCompat.StateMirror.GetValue(state)
                && !(bool)ToggleTreeCompat.StateMirrorParamActive.GetValue(state)
                && (float)ToggleTreeCompat.StateCycleOffset.GetValue(state) == 0f
                && !(bool)ToggleTreeCompat.StateCycleOffsetParamActive.GetValue(state);
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
                && other.Bindings.Overlaps(entry.Bindings));
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
                && other.Bindings.Overlaps(entry.Bindings));
        }

        internal static AnimatorControllerParameter FindParam(Snapshot snapshot, string name) {
            return ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.ControllerGetParam, snapshot.Fx, new object[] { name })
                as AnimatorControllerParameter;
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

        internal static bool MotionHasValidBinding(Snapshot snapshot, object motion) {
            return motion != null && (bool)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.HasValidBinding,
                snapshot.ValidateBindings,
                new[] { motion, snapshot.BindingRoot });
        }

        internal static bool MotionIsStatic(object motion) {
            return motion != null && (bool)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.MotionIsStatic, motion, null);
        }

        /**
         * The motion sampled at its final frame, or a fresh empty clip when there is none.
         * MotionExtensions.GetLastFrame is gone; VRCFury spells this EvaluateMotion(1) now
         * (it uses the same call wherever it needs a toggle's settled state).
         */
        internal static object LastFrameOrEmpty(object motion, string emptyName) {
            if (motion == null) return ToggleTreeCompat.NewEmptyClip(emptyName);
            return ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.MotionEvaluate, motion, new object[] { 1f });
        }

        /** True when every curve in the motion is a plain float curve (no material swaps, no AAPs). */
        internal static bool MotionIsPlainFloat(object motion) {
            if (motion == null) return true;
            var iterator = ToggleTreeCompat.NewClipsIterator();
            foreach (var clip in (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                         ToggleTreeCompat.ClipsFromMotion, iterator, new[] { motion })) {
                if (clip == null) continue;
                foreach (var entry in ClipCurveCompat.AllCurvesOf(clip)) {
                    var binding = ClipCurveCompat.TupleBinding(entry);
                    if (ClipCurveCompat.IsAnimatorBinding(binding)) return false;
                    var curve = ClipCurveCompat.TupleCurve(entry);
                    if (curve == null || !ClipCurveCompat.IsFloat(curve)) return false;
                }
            }
            return true;
        }
    }
}
