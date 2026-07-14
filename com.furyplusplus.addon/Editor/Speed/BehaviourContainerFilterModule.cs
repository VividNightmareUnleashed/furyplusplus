using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Several late controller services invoke VFLayer.RewriteBehaviours on every
     * layer even though only a small subset contains the requested behaviour type.
     * A cheap raw-array scan can prove the others empty without constructing
     * VRCFury's recursive immutable VFBehaviourContainer graph.
     */
    internal sealed class BehaviourContainerFilterModule : Module<BehaviourContainerFilterModule> {

        internal override string Id => "behaviourContainerFilter";
        internal override string DisplayName => "Behaviour container filter";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Skips behaviour-container graph builds for layers a raw array scan proves irrelevant.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            VfLayerCompat.EnsureResolved();
            BehaviourContainerFilterPatch.Install(harmony, compat);
        }

        internal override string ReportStats() {
            var stats = BehaviourContainerFilterPatch.LastStats;
            return stats == "none" ? null : stats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return BehaviourContainerFilterPatch.LastSkipped > 0
                ? ($"{BehaviourContainerFilterPatch.LastSkipped}/{BehaviourContainerFilterPatch.LastChecked} " +
                   "layer scans skipped last bake", BehaviourContainerFilterPatch.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class BehaviourContainerFilterPatch {
        private sealed class Context {
            internal string Name;
            internal Type BehaviourType;
            internal readonly Dictionary<int, bool> HasTargetByStateMachine =
                new Dictionary<int, bool>();
            internal int LayersChecked;
            internal int LayersSkipped;
        }

        [ThreadStatic] private static Context active;
        private static FieldInfo stateMachineField;
        private static object emptyContainers;
        private static Type playableLayerControlType;
        private static Type parameterDriverType;
        private static Type animatorLayerControlType;

        internal static string LastStats { get; private set; } = "none";
        internal static int LastSkipped { get; private set; }
        internal static int LastChecked { get; private set; }

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var containerGetter = VfLayerCompat.BehaviourContainersGetter;
            stateMachineField = VfLayerCompat.RootStateMachineField;
            emptyContainers = containerGetter == null
                ? null
                : ReflectionUtils.CreateEmptyImmutableSet(containerGetter.ReturnType);

            if (containerGetter == null || stateMachineField == null || emptyContainers == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            playableLayerControlType = ReflectionUtils.FindType(
                "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl"
            );
            parameterDriverType = ReflectionUtils.FindType(
                "VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver"
            );
            animatorLayerControlType = ReflectionUtils.FindType(
                "VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"
            );

            var actionApply = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.ActionConflictResolverService"),
                "Apply"
            );
            var syncedDriverApply = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.MakeAllSyncedDriversLocalService"),
                "Apply"
            );
            var layerControlFix = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.AnimatorLayerControlOffsetService"),
                "Fix"
            );

            if (playableLayerControlType == null || parameterDriverType == null || animatorLayerControlType == null
                                              || actionApply == null || syncedDriverApply == null
                                              || layerControlFix == null) {
                throw new InvalidOperationException("service target signature mismatch");
            }

            // The filter owns its own prefix on the getter so it works even when the
            // tracking behaviour index (which patches the same getter) is unavailable.
            // At most one of the two contexts is ever active: they cover different
            // build phases. The prefix uses an object-typed __result — a closed
            // generic patch method would not survive Harmony's token-based shared
            // state when the getter's patch list is re-read.
            harmony.Patch(
                containerGetter,
                prefix: new HarmonyMethod(typeof(BehaviourContainerFilterPatch), nameof(Filter))
            );

            PatchPhase(
                harmony,
                actionApply,
                nameof(BeginPlayableLayerControls)
            );
            PatchPhase(
                harmony,
                syncedDriverApply,
                nameof(BeginParameterDrivers)
            );
            PatchPhase(
                harmony,
                layerControlFix,
                nameof(BeginAnimatorLayerControls)
            );
        }

        private static void PatchPhase(
            Harmony harmony,
            MethodInfo method,
            string prefixName
        ) {
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BehaviourContainerFilterPatch), prefixName),
                finalizer: new HarmonyMethod(typeof(BehaviourContainerFilterPatch), nameof(End))
            );
        }

        private static void BeginPlayableLayerControls() {
            Begin(playableLayerControlType, "playableLayerControl");
        }

        private static void BeginParameterDrivers() {
            Begin(parameterDriverType, "parameterDriver");
        }

        private static void BeginAnimatorLayerControls() {
            Begin(animatorLayerControlType, "animatorLayerControl");
        }

        private static void Begin(Type behaviourType, string name) {
            active = BehaviourContainerFilterModule.Instance?.Enabled == true
                ? new Context { Name = name, BehaviourType = behaviourType }
                : null;
        }

        private static Exception End(Exception __exception) {
            var context = active;
            if (context != null) {
                LastStats = context.Name + "=" + context.LayersSkipped + "/" + context.LayersChecked;
                LastSkipped = context.LayersSkipped;
                LastChecked = context.LayersChecked;
            }
            active = null;
            return __exception;
        }

        private static bool Filter(object __instance, ref object __result) {
            var context = active;
            if (context == null || __instance == null) return true;

            try {
                var stateMachine = stateMachineField.GetValue(__instance) as AnimatorStateMachine;
                if (stateMachine == null) return true;

                context.LayersChecked++;
                var id = stateMachine.GetInstanceID();
                if (!context.HasTargetByStateMachine.TryGetValue(id, out var hasTarget)) {
                    hasTarget = HasTarget(
                        stateMachine,
                        context.BehaviourType,
                        new HashSet<int>()
                    );
                    context.HasTargetByStateMachine[id] = hasTarget;
                }
                if (hasTarget) return true;

                context.LayersSkipped++;
                __result = emptyContainers;
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn("Behaviour container filter fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        private static bool HasTarget(
            AnimatorStateMachine stateMachine,
            Type target,
            HashSet<int> visited
        ) {
            if (stateMachine == null || !visited.Add(stateMachine.GetInstanceID())) return false;

            if (ContainsInstanceOf(stateMachine.behaviours, target)) return true;

            foreach (var childState in stateMachine.states) {
                if (ContainsInstanceOf(childState.state?.behaviours, target)) return true;
            }

            foreach (var childStateMachine in stateMachine.stateMachines) {
                if (HasTarget(childStateMachine.stateMachine, target, visited)) return true;
            }
            return false;
        }

        // Plain loop instead of Enumerable.Any: this runs for every state on every
        // uncached layer scan, and the captured-lambda closure allocations add up.
        private static bool ContainsInstanceOf(StateMachineBehaviour[] behaviours, Type target) {
            if (behaviours == null) return false;
            foreach (var behaviour in behaviours) {
                if (behaviour != null && target.IsInstanceOfType(behaviour)) return true;
            }
            return false;
        }
    }
}
