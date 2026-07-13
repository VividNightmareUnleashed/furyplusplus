using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * Memoizes ValidateBindingsService.IsValid during the FullController merge phase.
     * CreateNearestMatchPathRewriter probes IsValid once per ancestor level per distinct
     * binding per clip — each probe is a full transform-path Find plus GetComponent — and
     * its own cache neither spans clips nor merges. The avatar hierarchy is verifiably
     * stable during this phase (object moves happen later, at SecurityRestricted/
     * ArmatureLink), so the answer for a binding cannot change; a defensive flush on any
     * ObjectMoveService.Move keeps us honest if upstream ever reorders.
     */
    internal sealed class MergePathCacheModule : Module {
        internal static MergePathCacheModule Instance { get; private set; }

        internal MergePathCacheModule() {
            Instance = this;
        }

        internal override string Id => "mergePathCache";
        internal override string DisplayName => "Full Controller merge path cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Caches binding-path validation during Full Controller merges — the dominant " +
            "cost on avatars assembled from many prefab controllers.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            MergePathCachePatch.Install(harmony, compat);
        }
    }

    internal static class MergePathCachePatch {
        [ThreadStatic] private static Dictionary<EditorCurveBinding, bool> memo;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var builderType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Feature.FullControllerBuilder"), "VF.Feature.FullControllerBuilder");
            var validateType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ValidateBindingsService"), "VF.Service.ValidateBindingsService");
            var moveServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ObjectMoveService"), "VF.Service.ObjectMoveService");

            var apply = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(builderType, "Apply"), "FullControllerBuilder.Apply()");
            var isValid = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(validateType, "IsValid",
                    method => method.GetParameters().Length == 1
                              && method.GetParameters()[0].ParameterType == typeof(EditorCurveBinding)
                              && method.ReturnType == typeof(bool)),
                "ValidateBindingsService.IsValid(EditorCurveBinding)");
            var move = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(moveServiceType, "Move",
                    method => method.GetParameters().Length == 5),
                "ObjectMoveService.Move(...)");

            harmony.Patch(
                apply,
                prefix: new HarmonyMethod(typeof(MergePathCachePatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(MergePathCachePatch), nameof(End))
            );
            harmony.Patch(
                isValid,
                prefix: new HarmonyMethod(typeof(MergePathCachePatch), nameof(IsValidPrefix)),
                postfix: new HarmonyMethod(typeof(MergePathCachePatch), nameof(IsValidPostfix))
            );
            harmony.Patch(
                move,
                postfix: new HarmonyMethod(typeof(MergePathCachePatch), nameof(Flush))
            );
        }

        private static void Begin() {
            memo = MergePathCacheModule.Instance?.Enabled == true
                ? new Dictionary<EditorCurveBinding, bool>()
                : null;
        }

        private static Exception End(Exception __exception) {
            memo = null;
            return __exception;
        }

        private static void Flush() {
            // Hierarchy mutated mid-phase (unexpected at 1.1363) — drop everything.
            memo?.Clear();
        }

        private static bool IsValidPrefix(EditorCurveBinding binding, ref bool __result) {
            var localMemo = memo;
            if (localMemo == null) return true;
            if (!localMemo.TryGetValue(binding, out var cached)) return true;
            __result = cached;
            return false;
        }

        private static void IsValidPostfix(EditorCurveBinding binding, bool __result) {
            var localMemo = memo;
            if (localMemo != null) localMemo[binding] = __result;
        }
    }
}
