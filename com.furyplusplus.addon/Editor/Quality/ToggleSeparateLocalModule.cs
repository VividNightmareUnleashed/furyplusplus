using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Converts the 3-state layers that "Separate Local State" toggles produce
     * (Off default / On Local / On Remote, instant transitions) into one direct-blendtree
     * entry whose on-motion is a 1D selector on IsLocal. VRCFury's own layer-to-tree
     * optimizer rejects these ("Contains 3 states"), yet with static motions and zero
     * transition times the tree is behaviorally equivalent — IsLocal is constant per
     * client, so all blend weights stay binary.
     *
     * Default OFF: unlike the ports, this changes animator topology for a whole class of
     * hand-tested toggles at once; enable after checking your own local/remote variants.
     */
    internal sealed class ToggleSeparateLocalModule : Module {
        internal static ToggleSeparateLocalModule Instance { get; private set; }

        internal ToggleSeparateLocalModule() {
            Instance = this;
        }

        internal override string Id => "toggleSeparateLocal";
        internal override string DisplayName => "Separate-local toggles → blendtree";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override string Description =>
            "Converts 'Separate Local State' toggles (Off / On Local / On Remote with instant " +
            "transitions and static motions) into a direct-blendtree branch selected by IsLocal, " +
            "removing one animator layer per toggle. On/off changes stay instant and per-client " +
            "content is unchanged.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ToggleTreeCompat.DemandCore();
            BuildPhaseHooks.RegisterAfter("LayerToTree", Id, _ => ToggleSeparateLocalPass.Run());
        }

        internal override string ReportStats() {
            return ToggleSeparateLocalPass.LastStats;
        }
    }

    internal static class ToggleSeparateLocalPass {
        internal static string LastStats;

        private sealed class Match {
            internal ToggleConversionRuntime.Entry Entry;
            internal AnimatorState LocalState;
            internal AnimatorState RemoteState;
            internal AnimatorCondition OnCondition;
        }

        internal static void Run() {
            if (ToggleSeparateLocalModule.Instance?.Enabled != true) return;
            LastStats = null;

            try {
                var snapshot = ToggleConversionRuntime.Take();
                if (snapshot == null) return;

                object dbt = null;
                var converted = new List<string>();
                foreach (var entry in snapshot.Layers) {
                    var match = TryMatch(snapshot, entry);
                    if (match == null) continue;
                    if (dbt == null) {
                        dbt = ToggleTreeCompat.CreateDbtLayer(snapshot.Fx, "FuryPlusPlus Local/Remote Toggles");
                    }
                    Convert(match, dbt);
                    ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.LayerRemove, entry.VfLayer, null);
                    entry.Converted = true;
                    converted.Add(entry.Name);
                }

                if (converted.Count > 0) {
                    Log.Info($"Converted {converted.Count} separate-local toggle layer(s) to blendtree: " +
                             string.Join(", ", converted));
                    LastStats = $"converted={converted.Count}";
                }
            } catch (System.Exception e) {
                Log.Warn("Separate-local toggle conversion skipped: " + e.Message);
            }
        }

        private static Match TryMatch(ToggleConversionRuntime.Snapshot snapshot, ToggleConversionRuntime.Entry entry) {
            var machine = entry.Machine;
            if (machine == null || machine.states.Length != 3) return null;
            if (machine.entryTransitions.Length != 0) return null;
            if (!ToggleConversionRuntime.PassesCommonLayerGuards(snapshot, entry)) return null;

            var states = machine.states.Select(child => child.state).ToArray();
            var off = machine.defaultState;
            if (off == null || !states.Contains(off)) return null;
            if (off.motion != null) return null;

            // Off must branch to both on-states, each gated on the shared param + IsLocal.
            var offTransitions = off.transitions;
            if (offTransitions.Length != 2) return null;
            var branches = new List<(AnimatorState Destination, AnimatorCondition On, AnimatorCondition IsLocal)>();
            foreach (var transition in offTransitions) {
                if (transition.isExit || transition.hasExitTime || transition.duration != 0) return null;
                if (transition.destinationState == null || transition.destinationState == off) return null;
                if (transition.conditions.Length != 2) return null;
                var isLocalConditions = transition.conditions.Where(IsIsLocalCondition).ToArray();
                var otherConditions = transition.conditions.Where(c => !IsIsLocalCondition(c)).ToArray();
                if (isLocalConditions.Length != 1 || otherConditions.Length != 1) return null;
                branches.Add((transition.destinationState, otherConditions[0], isLocalConditions[0]));
            }
            if (branches[0].Destination == branches[1].Destination) return null;
            if (!ToggleConversionRuntime.ConditionsEqual(branches[0].On, branches[1].On)) return null;
            if (branches[0].IsLocal.mode == branches[1].IsLocal.mode) return null;

            var onCondition = branches[0].On;
            if (onCondition.parameter == "IsLocal") return null;
            var parameter = ToggleConversionRuntime.FindParam(snapshot, onCondition.parameter);
            if (parameter == null) return null;
            if (onCondition.mode == AnimatorConditionMode.If) {
                if (parameter.type != AnimatorControllerParameterType.Bool) return null;
            } else if (onCondition.mode == AnimatorConditionMode.NotEqual && onCondition.threshold == 0) {
                if (parameter.type != AnimatorControllerParameterType.Int) return null;
            } else {
                return null;
            }
            // Mirror of stock: int-typed VRC built-ins are likely >1, semantics unclear.
            if (ToggleTreeCompat.VrchatGlobalParams.Contains(onCondition.parameter)
                && parameter.type == AnimatorControllerParameterType.Int) return null;

            var local = branches.First(b => b.IsLocal.mode == AnimatorConditionMode.If);
            var remote = branches.First(b => b.IsLocal.mode == AnimatorConditionMode.IfNot);

            // Each on-state exits on exactly ¬(param ∧ isLocalSide): two single-condition exits.
            foreach (var branch in branches) {
                var state = branch.Destination;
                if (state.motion != null && !ToggleConversionRuntime.MotionIsStatic(state.motion)) return null;
                var exits = state.transitions;
                if (exits.Length != 2) return null;
                if (exits.Any(t => !t.isExit || t.hasExitTime || t.duration != 0 || t.conditions.Length != 1)) {
                    return null;
                }
                var negatedOn = ToggleConversionRuntime.Negate(branch.On);
                var negatedLocal = ToggleConversionRuntime.Negate(branch.IsLocal);
                if (negatedOn == null || negatedLocal == null) return null;
                var exitConditions = exits.Select(t => t.conditions[0]).ToArray();
                var matchesNegation =
                    (ToggleConversionRuntime.ConditionsEqual(exitConditions[0], negatedOn.Value)
                     && ToggleConversionRuntime.ConditionsEqual(exitConditions[1], negatedLocal.Value))
                    || (ToggleConversionRuntime.ConditionsEqual(exitConditions[1], negatedOn.Value)
                        && ToggleConversionRuntime.ConditionsEqual(exitConditions[0], negatedLocal.Value));
                if (!matchesNegation) return null;
            }

            if (!ToggleConversionRuntime.MotionHasValidBinding(snapshot, local.Destination.motion)
                && !ToggleConversionRuntime.MotionHasValidBinding(snapshot, remote.Destination.motion)) {
                return null;
            }
            if (ToggleConversionRuntime.SharesBindingsWithHigherLayer(snapshot, entry)) return null;

            return new Match {
                Entry = entry,
                LocalState = local.Destination,
                RemoteState = remote.Destination,
                OnCondition = onCondition
            };
        }

        private static bool IsIsLocalCondition(AnimatorCondition condition) {
            return condition.parameter == "IsLocal"
                   && (condition.mode == AnimatorConditionMode.If
                       || condition.mode == AnimatorConditionMode.IfNot);
        }

        private static void Convert(Match match, object dbt) {
            var layerName = match.Entry.Name;
            var remoteMotion = ToggleConversionRuntime.LastFrameOrEmpty(
                match.RemoteState.motion, $"{layerName} (remote off)");
            var localMotion = ToggleConversionRuntime.LastFrameOrEmpty(
                match.LocalState.motion, $"{layerName} (local off)");

            var selector = ToggleTreeCompat.Tree1DCreate.Invoke(
                null, new object[] { $"{layerName} local/remote", "IsLocal" });
            ToggleTreeCompat.Tree1DAdd.Invoke(selector, new object[] { 0f, remoteMotion });
            ToggleTreeCompat.Tree1DAdd.Invoke(selector, new object[] { 1f, localMotion });
            var selectorMotion = ToggleTreeCompat.TreeToMotion(selector);

            if (match.OnCondition.mode == AnimatorConditionMode.If) {
                // Off writes nothing, exactly like the original Off state: one-sided add.
                ToggleTreeCompat.DirectAddWeighted.Invoke(
                    dbt, new object[] { match.OnCondition.parameter, selectorMotion });
            } else {
                // Int param, on when != 0 (mirror of stock's NotEqual→Equals handling).
                var offClip = ToggleTreeCompat.NewEmptyClip($"{layerName} (off)");
                var select = ToggleTreeCompat.EqualsSelect(
                    match.OnCondition.parameter, 0f, offClip, selectorMotion);
                ToggleTreeCompat.DirectAddOne.Invoke(dbt, new object[] { select });
            }
        }
    }
}
