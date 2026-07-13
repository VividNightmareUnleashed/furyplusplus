using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;

namespace FuryPlusPlus {
    /**
     * Memoizes VFController.GetLayers(): the underlying AnimatorController.layers getter
     * marshals a fresh copy of every layer struct on EVERY access (there is no stable
     * array to reference-compare — the getter call IS the cost), and GetLayers is called
     * pervasively. VFLayer wrappers are value-equal via rootStateMachine (==/Equals/
     * GetHashCode overridden; audited: no caller depends on wrapper reference identity),
     * so cached wrappers can be handed back — in a fresh array copy per call, preserving
     * any caller's freedom to mutate the array object itself.
     *
     * Invalidation: version stamps bumped by postfixes on every enumerated layers-array
     * write site, plus a full clear at every action boundary and build begin/end as the
     * backstop for anything unenumerated.
     */
    internal sealed class GetLayersMemoModule : Module {
        internal static GetLayersMemoModule Instance { get; private set; }

        internal GetLayersMemoModule() {
            Instance = this;
        }

        internal override string Id => "getLayersMemo";
        internal override string DisplayName => "Controller layer-list cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Caches VFController.GetLayers() between mutations, avoiding a native marshal " +
            "of every layer per call.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            GetLayersMemoPatch.Install(harmony, compat);
        }
    }

    internal static class GetLayersMemoPatch {
        private sealed class Entry {
            internal int Version;
            internal Array Wrappers;
        }

        [ThreadStatic] private static Dictionary<AnimatorController, int> versions;
        [ThreadStatic] private static Dictionary<AnimatorController, Entry> cache;

        private static FieldInfo vfControllerCtrlField;
        private static FieldInfo vfLayerCtrlField;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"), "VF.Utils.Controller.VFController");
            var vfLayerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFLayer"), "VF.Utils.Controller.VFLayer");
            vfControllerCtrlField = ReflectionUtils.Demand(
                vfControllerType.GetField("ctrl", any), "VFController.ctrl");
            vfLayerCtrlField = ReflectionUtils.Demand(
                vfLayerType.GetField("ctrl", any), "VFLayer.ctrl");

            var getLayers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                    method => method.GetParameters().Length == 0),
                "VFController.GetLayers()");

            // Every enumerated write site of ctrl.layers. A miss here is bounded by the
            // action-boundary clears; all bumps are postfixes (fire after the mutation).
            var bumpTargets = new List<(MethodInfo Method, bool IsLayer)>();
            void AddBump(Type type, string name, bool isLayer, Func<MethodInfo, bool> predicate = null) {
                var method = ReflectionUtils.FindUniqueMethod(type, name, m => predicate == null || predicate(m));
                bumpTargets.Add((ReflectionUtils.Demand(method, type.Name + "." + name), isLayer));
            }
            AddBump(vfLayerType, "WithLayer", true);
            AddBump(vfLayerType, "Move", true);
            AddBump(vfLayerType, "Remove", true);
            AddBump(vfControllerType, "NewLayer", false);
            AddBump(vfControllerType, "FixNullStateMachines", false);
            AddBump(vfControllerType, "ReplaceSyncedLayers", false);
            AddBump(vfControllerType, "RemoveDuplicateStateMachines", false);

            var namesServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.MakeControllerNamesUniqueService"),
                "VF.Service.MakeControllerNamesUniqueService");
            var namesApply = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(namesServiceType, "Apply"),
                "MakeControllerNamesUniqueService.Apply()");
            var rewriteInternals = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(
                    ReflectionUtils.FindType("VF.Utils.MutableManager"), "RewriteInternals",
                    method => method.GetParameters().Length == 2),
                "MutableManager.RewriteInternals(...)");

            harmony.Patch(
                compatibility.RunMain,
                prefix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(End))
            );
            harmony.Patch(
                compatibility.ActionCall,
                prefix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(ActionBoundary))
            );
            harmony.Patch(
                getLayers,
                prefix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(GetLayersPrefix)),
                postfix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(GetLayersPostfix))
            );
            foreach (var (method, isLayer) in bumpTargets) {
                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(GetLayersMemoPatch),
                        isLayer ? nameof(BumpFromLayer) : nameof(BumpFromController))
                );
            }
            // TakeOwnershipOf mutates BOTH controllers' layer arrays.
            var takeOwnership = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfControllerType, "TakeOwnershipOf",
                    method => method.GetParameters().Length >= 1),
                "VFController.TakeOwnershipOf(...)");
            harmony.Patch(
                takeOwnership,
                postfix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(BumpBoth))
            );
            harmony.Patch(
                namesApply,
                postfix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(BumpAll))
            );
            harmony.Patch(
                rewriteInternals,
                postfix: new HarmonyMethod(typeof(GetLayersMemoPatch), nameof(BumpAll))
            );
        }

        private static void Begin() {
            var enabled = GetLayersMemoModule.Instance?.Enabled == true;
            versions = enabled ? new Dictionary<AnimatorController, int>() : null;
            cache = enabled ? new Dictionary<AnimatorController, Entry>() : null;
        }

        private static Exception End(Exception __exception) {
            versions = null;
            cache = null;
            return __exception;
        }

        private static void ActionBoundary() {
            cache?.Clear();
        }

        private static bool GetLayersPrefix(object __instance, ref object __result) {
            var localCache = cache;
            if (localCache == null) return true;
            try {
                if (!(vfControllerCtrlField.GetValue(__instance) is AnimatorController controller)) return true;
                if (!localCache.TryGetValue(controller, out var entry)) return true;
                var version = 0;
                versions?.TryGetValue(controller, out version);
                if (entry.Version != version) return true;
                __result = entry.Wrappers.Clone();
                return false;
            } catch {
                cache = null;
                return true;
            }
        }

        private static void GetLayersPostfix(object __instance, object __result) {
            var localCache = cache;
            if (localCache == null || !(__result is Array wrappers)) return;
            try {
                if (!(vfControllerCtrlField.GetValue(__instance) is AnimatorController controller)) return;
                var version = 0;
                versions?.TryGetValue(controller, out version);
                localCache[controller] = new Entry {
                    Version = version,
                    Wrappers = (Array)wrappers.Clone()
                };
            } catch {
                cache = null;
            }
        }

        private static void BumpFromLayer(object __instance) {
            Bump(vfLayerCtrlField, __instance);
        }

        private static void BumpFromController(object __instance) {
            Bump(vfControllerCtrlField, __instance);
        }

        private static void BumpBoth(object __instance, object __0) {
            Bump(vfControllerCtrlField, __instance);
            Bump(vfControllerCtrlField, __0);
        }

        private static void BumpAll() {
            versions?.Clear();
            cache?.Clear();
        }

        private static void Bump(FieldInfo ctrlField, object owner) {
            var localVersions = versions;
            if (localVersions == null || owner == null) return;
            try {
                if (ctrlField.GetValue(owner) is AnimatorController controller) {
                    localVersions.TryGetValue(controller, out var version);
                    localVersions[controller] = version + 1;
                }
            } catch {
                versions = null;
                cache = null;
            }
        }
    }
}
