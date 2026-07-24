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
    internal sealed class ToggleSeparateLocalModule : Module<ToggleSeparateLocalModule> {

        internal override string Id => "toggleSeparateLocal";
        internal override string DisplayName => "Separate-local toggles → blendtree";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Animator layers";
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

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return ToggleSeparateLocalPass.LastConverted > 0
                ? ($"{ToggleSeparateLocalPass.LastConverted} toggles → blendtree last bake",
                    ToggleSeparateLocalPass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class ToggleSeparateLocalPass {
        internal static string LastStats;
        internal static int LastConverted;

        private sealed class Match {
            internal ToggleConversionRuntime.Entry Entry;
            internal object LocalState;   // VFState
            internal object RemoteState;  // VFState
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

                LastConverted = converted.Count;
                LastStats = $"converted={converted.Count} ({ToggleConversionRuntime.LastSnapshotSummary})";
                if (converted.Count > 0) {
                    Log.Info($"Converted {converted.Count} separate-local toggle layer(s) to blendtree: " +
                             string.Join(", ", converted));
                }
            } catch (System.Exception e) {
                Log.Warn("Separate-local toggle conversion skipped: " + e.Message);
            }
        }

        private static Match TryMatch(ToggleConversionRuntime.Snapshot snapshot, ToggleConversionRuntime.Entry entry) {
            var machine = entry.StateMachine;
            if (machine == null) return null;
            var states = ToggleConversionRuntime.StatesOf(machine);
            if (states.Count != 3) return null;
            if (ToggleConversionRuntime.CountOf(
                    ToggleTreeCompat.SmEntryTransitions.GetValue(machine)) != 0) return null;
            if (!ToggleConversionRuntime.PassesCommonLayerGuards(snapshot, entry)) return null;

            var off = ToggleTreeCompat.SmDefaultState.GetValue(machine);
            if (off == null || !states.Any(state => ReferenceEquals(state, off))) return null;
            if (ToggleConversionRuntime.MotionOf(off) != null) return null;

            // Off must branch to both on-states, each gated on the shared param + IsLocal.
            var offTransitions = ToggleConversionRuntime.TransitionsOf(off);
            if (offTransitions.Count != 2) return null;
            var branches = new List<(object Destination, AnimatorCondition On, AnimatorCondition IsLocal)>();
            foreach (var transition in offTransitions) {
                if (ToggleConversionRuntime.IsExit(transition)
                    || ToggleConversionRuntime.HasExitTime(transition)
                    || ToggleConversionRuntime.DurationOf(transition) != 0) return null;
                var destination = ToggleConversionRuntime.DestinationOf(transition);
                if (destination == null || ReferenceEquals(destination, off)) return null;
                var conditions = ToggleConversionRuntime.ConditionsOf(transition);
                if (conditions.Length != 2) return null;
                var isLocalConditions = conditions.Where(IsIsLocalCondition).ToArray();
                var otherConditions = conditions.Where(c => !IsIsLocalCondition(c)).ToArray();
                if (isLocalConditions.Length != 1 || otherConditions.Length != 1) return null;
                branches.Add((destination, otherConditions[0], isLocalConditions[0]));
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
                var motion = ToggleConversionRuntime.MotionOf(state);
                if (motion != null && !ToggleConversionRuntime.MotionIsStatic(motion)) return null;
                var exits = ToggleConversionRuntime.TransitionsOf(state);
                if (exits.Count != 2) return null;
                if (exits.Any(t => !ToggleConversionRuntime.IsExit(t)
                                   || ToggleConversionRuntime.HasExitTime(t)
                                   || ToggleConversionRuntime.DurationOf(t) != 0
                                   || ToggleConversionRuntime.ConditionsOf(t).Length != 1)) {
                    return null;
                }
                var negatedOn = ToggleConversionRuntime.Negate(branch.On);
                var negatedLocal = ToggleConversionRuntime.Negate(branch.IsLocal);
                if (negatedOn == null || negatedLocal == null) return null;
                var exitConditions = exits.Select(t => ToggleConversionRuntime.ConditionsOf(t)[0]).ToArray();
                var matchesNegation =
                    (ToggleConversionRuntime.ConditionsEqual(exitConditions[0], negatedOn.Value)
                     && ToggleConversionRuntime.ConditionsEqual(exitConditions[1], negatedLocal.Value))
                    || (ToggleConversionRuntime.ConditionsEqual(exitConditions[1], negatedOn.Value)
                        && ToggleConversionRuntime.ConditionsEqual(exitConditions[0], negatedLocal.Value));
                if (!matchesNegation) return null;
            }

            if (!ToggleConversionRuntime.MotionHasValidBinding(
                    snapshot, ToggleConversionRuntime.MotionOf(local.Destination))
                && !ToggleConversionRuntime.MotionHasValidBinding(
                    snapshot, ToggleConversionRuntime.MotionOf(remote.Destination))) {
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
                ToggleConversionRuntime.MotionOf(match.RemoteState), $"{layerName} (remote off)");
            var localMotion = ToggleConversionRuntime.LastFrameOrEmpty(
                ToggleConversionRuntime.MotionOf(match.LocalState), $"{layerName} (local off)");

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
