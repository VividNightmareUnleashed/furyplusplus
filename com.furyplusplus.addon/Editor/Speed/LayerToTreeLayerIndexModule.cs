using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;

namespace FuryPlusPlus {
    /**
     * VFLayer.GetLayerId and VFLayer.Exists both search AnimatorController.layers.
     * LayerToTreeService calls them inside its layer-conflict nested loop, turning a
     * cheap conflict check into thousands of repeated controller-layer scans. Keep a
     * short-lived state-machine-to-index table while LayerToTreeService.Apply runs.
     */
    internal sealed class LayerToTreeLayerIndexModule : Module<LayerToTreeLayerIndexModule> {

        internal override string Id => "layerToTreeLayerIndex";
        internal override string DisplayName => "Layer-to-tree layer index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "O(1) layer lookups during VRCFury's layer-to-blendtree conversion pass.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            LayerToTreeLayerIndexPatch.Install(harmony, compat);
        }
    }

    internal static class LayerToTreeLayerIndexPatch {
        private sealed class ControllerIndex {
            internal AnimatorController Controller;
            internal readonly Dictionary<int, int> LayerByStateMachineId =
                new Dictionary<int, int>();
            // Retained across rebuilds so a wrapper for a layer removed earlier in the
            // pass can return Exists=false without forcing another controller scan.
            internal readonly HashSet<int> ObservedStateMachineIds = new HashSet<int>();
        }

        private sealed class Context {
            internal readonly Dictionary<int, ControllerIndex> Controllers =
                new Dictionary<int, ControllerIndex>();
        }

        [ThreadStatic] private static Context active;

        private static FieldInfo controllerField;
        private static FieldInfo stateMachineField;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var serviceType = ReflectionUtils.FindType("VF.Service.LayerToTreeService");
            var layerType = ReflectionUtils.FindType("VF.Utils.Controller.VFLayer");

            var apply = ReflectionUtils.FindNoArgVoid(serviceType, "Apply");
            var getLayerId = ReflectionUtils.FindMethodWithSignature(layerType, "GetLayerId", typeof(int));
            var exists = ReflectionUtils.FindMethodWithSignature(layerType, "Exists", typeof(bool));
            var remove = ReflectionUtils.FindNoArgVoid(layerType, "Remove");
            var move = ReflectionUtils.FindMethodWithSignature(layerType, "Move", typeof(void), typeof(int));

            VfLayerCompat.EnsureResolved();
            controllerField = VfLayerCompat.CtrlField;
            stateMachineField = VfLayerCompat.RootStateMachineField;

            if (apply == null || getLayerId == null || exists == null || remove == null || move == null
                              || controllerField == null || stateMachineField == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                apply,
                prefix: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(End))
            );
            harmony.Patch(
                getLayerId,
                prefix: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(GetLayerId))
            );
            harmony.Patch(
                exists,
                prefix: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(Exists))
            );
            harmony.Patch(
                remove,
                postfix: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(LayerRemoved))
            );
            harmony.Patch(
                move,
                postfix: new HarmonyMethod(typeof(LayerToTreeLayerIndexPatch), nameof(LayerMoved))
            );
        }

        private static void Begin() {
            active = LayerToTreeLayerIndexModule.Instance?.Enabled == true ? new Context() : null;
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        private static bool GetLayerId(object __instance, ref int __result) {
            var context = active;
            if (context == null) return true;

            try {
                var controller = controllerField.GetValue(__instance) as AnimatorController;
                var stateMachine = stateMachineField.GetValue(__instance) as AnimatorStateMachine;
                if (controller == null || stateMachine == null) return true;

                var index = GetOrBuild(context, controller);
                var stateMachineId = stateMachine.GetInstanceID();
                if (!index.LayerByStateMachineId.TryGetValue(stateMachineId, out __result)) {
                    if (index.ObservedStateMachineIds.Contains(stateMachineId)) return true;

                    // A layer may have been appended since this controller was first seen
                    // (the direct-tree layer itself is created lazily). Rebuild on a miss.
                    Rebuild(index);
                    if (!index.LayerByStateMachineId.TryGetValue(stateMachineId, out __result)) {
                        index.ObservedStateMachineIds.Add(stateMachineId);
                        // Preserve VRCFury's exception behavior for a removed/unknown layer.
                        return true;
                    }
                }
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn("Layer-to-tree layer index fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        private static bool Exists(object __instance, ref bool __result) {
            var context = active;
            if (context == null) return true;

            try {
                var controller = controllerField.GetValue(__instance) as AnimatorController;
                var stateMachine = stateMachineField.GetValue(__instance) as AnimatorStateMachine;
                if (controller == null || stateMachine == null) return true;

                var index = GetOrBuild(context, controller);
                var stateMachineId = stateMachine.GetInstanceID();
                if (index.LayerByStateMachineId.ContainsKey(stateMachineId)) {
                    __result = true;
                    return false;
                }

                if (index.ObservedStateMachineIds.Contains(stateMachineId)) {
                    __result = false;
                    return false;
                }

                // This also discovers layers appended by the lazy direct-tree creation.
                Rebuild(index);
                __result = index.LayerByStateMachineId.ContainsKey(stateMachineId);
                if (!__result) index.ObservedStateMachineIds.Add(stateMachineId);
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn("Layer-to-tree layer index fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        private static void LayerMoved(object __instance) {
            var context = active;
            if (context == null) return;

            try {
                var controller = controllerField.GetValue(__instance) as AnimatorController;
                if (controller == null) return;
                if (context.Controllers.TryGetValue(controller.GetInstanceID(), out var index)) {
                    Rebuild(index);
                }
            } catch (Exception e) {
                active = null;
                Log.Warn("Layer-to-tree layer index fell back to VRCFury: " + e.Message);
            }
        }

        private static void LayerRemoved(object __instance) {
            var context = active;
            if (context == null) return;

            try {
                var controller = controllerField.GetValue(__instance) as AnimatorController;
                if (controller == null) return;
                if (!context.Controllers.TryGetValue(controller.GetInstanceID(), out var index)) return;

                var stateMachine = stateMachineField.GetValue(__instance) as AnimatorStateMachine;
                if (stateMachine != null
                    && index.LayerByStateMachineId.TryGetValue(stateMachine.GetInstanceID(), out var removedIndex)) {
                    // LayerToTreeService removes many layers; shifting the retained indexes
                    // avoids re-cloning Controller.layers once per removal.
                    index.LayerByStateMachineId.Remove(stateMachine.GetInstanceID());
                    foreach (var key in index.LayerByStateMachineId.Keys.ToList()) {
                        var layerIndex = index.LayerByStateMachineId[key];
                        if (layerIndex > removedIndex) index.LayerByStateMachineId[key] = layerIndex - 1;
                    }
                    return;
                }

                // A removal the index never observed: fall back to a full rebuild.
                Rebuild(index);
            } catch (Exception e) {
                active = null;
                Log.Warn("Layer-to-tree layer index fell back to VRCFury: " + e.Message);
            }
        }

        private static ControllerIndex GetOrBuild(Context context, AnimatorController controller) {
            var controllerId = controller.GetInstanceID();
            if (context.Controllers.TryGetValue(controllerId, out var existing)) return existing;

            var created = new ControllerIndex { Controller = controller };
            Rebuild(created);
            context.Controllers.Add(controllerId, created);
            return created;
        }

        private static void Rebuild(ControllerIndex index) {
            index.LayerByStateMachineId.Clear();
            var layers = index.Controller.layers;
            for (var i = 0; i < layers.Length; i++) {
                var stateMachine = layers[i].stateMachine;
                if (stateMachine != null) {
                    var stateMachineId = stateMachine.GetInstanceID();
                    index.LayerByStateMachineId[stateMachineId] = i;
                    index.ObservedStateMachineIds.Add(stateMachineId);
                }
            }
        }
    }
}
