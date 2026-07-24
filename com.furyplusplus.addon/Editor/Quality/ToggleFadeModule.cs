using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Converts pure-crossfade transition toggles (Off → "On In" → On → "On Out" where
     * the in/out states are empty and only the transition durations fade) into a
     * time-smoothed float driving a 1D blendtree, removing one 4-state animator layer per
     * toggle. VRCFury's own optimizer rejects these ("transition with a non-0 duration").
     *
     * HONEST DELTAS (why this defaults OFF and needs the user's eye):
     *  - the smoothed ramp is exponential-ish, not the original linear crossfade;
     *  - interrupting mid-fade reverses immediately instead of completing;
     *  - mid-fade values blend from scene rest values, not from lower animator layers
     *    (mitigated: toggles sharing bindings with any other layer are skipped);
     *  - only symmetric fades convert (in-time == out-time); asymmetric ones are skipped.
     */
    internal sealed class ToggleFadeModule : Module<ToggleFadeModule> {

        internal override string Id => "toggleFadeTrees";
        internal override string DisplayName => "Fade toggles → smoothed blendtree";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Animator layers";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override string Description =>
            "Converts pure-crossfade transition toggles (empty in/out states, symmetric blend " +
            "times, float-only static content) into a time-smoothed parameter driving a 1D " +
            "blendtree, removing one 4-state layer per toggle. The fade curve becomes " +
            "exponential-ish instead of linear and mid-fade interruptions reverse immediately — " +
            "judge the feel on your own avatar before leaving this on.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ToggleTreeCompat.DemandCore();
            ReflectionUtils.Demand(ToggleTreeCompat.Smooth, "SmoothingService.Smooth(...)");
            BuildPhaseHooks.RegisterAfter("LayerToTree", Id, _ => ToggleFadePass.Run());
        }

        internal override string ReportStats() {
            return ToggleFadePass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return ToggleFadePass.LastConverted > 0
                ? ($"{ToggleFadePass.LastConverted} fade toggles converted last bake", ToggleFadePass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class ToggleFadePass {
        internal static string LastStats;
        internal static int LastConverted;

        private sealed class Match {
            internal ToggleConversionRuntime.Entry Entry;
            internal object OnState;   // VFState
            internal AnimatorCondition OnCondition;
            internal float FadeSeconds;
        }

        internal static void Run() {
            if (ToggleFadeModule.Instance?.Enabled != true) return;
            LastStats = null;

            try {
                var snapshot = ToggleConversionRuntime.Take();
                if (snapshot == null) return;
                var smoothingService = BuildPhaseHooks.GetService("VF.Service.SmoothingService");
                if (smoothingService == null) return;

                object dbt = null;
                var converted = new List<string>();
                var skippedAsymmetric = 0;
                foreach (var entry in snapshot.Layers) {
                    var match = TryMatch(snapshot, entry, ref skippedAsymmetric);
                    if (match == null) continue;
                    if (dbt == null) {
                        dbt = ToggleTreeCompat.CreateDbtLayer(snapshot.Fx, "FuryPlusPlus Fade Toggles");
                    }
                    Convert(snapshot, match, dbt, smoothingService);
                    ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.LayerRemove, entry.VfLayer, null);
                    entry.Converted = true;
                    converted.Add(entry.Name);
                }

                if (converted.Count > 0) {
                    Log.Info($"Converted {converted.Count} fade toggle layer(s) to smoothed blendtree: " +
                             string.Join(", ", converted));
                }
                if (skippedAsymmetric > 0) {
                    Log.Info($"Skipped {skippedAsymmetric} fade toggle(s) with asymmetric in/out times " +
                             "(not converted; stock layers kept).");
                }
                LastConverted = converted.Count;
                LastStats = $"converted={converted.Count} skippedAsymmetric={skippedAsymmetric} " +
                            $"({ToggleConversionRuntime.LastSnapshotSummary})";
            } catch (System.Exception e) {
                Log.Warn("Fade toggle conversion skipped: " + e.Message);
            }
        }

        private static Match TryMatch(
            ToggleConversionRuntime.Snapshot snapshot,
            ToggleConversionRuntime.Entry entry,
            ref int skippedAsymmetric
        ) {
            var machine = entry.StateMachine;
            if (machine == null) return null;
            var states = ToggleConversionRuntime.StatesOf(machine);
            if (states.Count != 4) return null;
            if (!ToggleConversionRuntime.PassesCommonLayerGuards(snapshot, entry)) return null;

            // Off is the effective entry state. Default-on toggles make On the default state
            // and add a single unconditional entry transition to Off (ToggleBuilder shape).
            object off;
            var entryTransitions = new List<object>();
            foreach (var transition in (System.Collections.IEnumerable)
                     ToggleTreeCompat.SmEntryTransitions.GetValue(machine)) {
                entryTransitions.Add(transition);
            }
            if (entryTransitions.Count == 0) {
                off = ToggleTreeCompat.SmDefaultState.GetValue(machine);
            } else if (entryTransitions.Count == 1
                       && ToggleConversionRuntime.ConditionsOf(entryTransitions[0]).Length == 0
                       && ToggleConversionRuntime.DestinationOf(entryTransitions[0]) != null) {
                off = ToggleConversionRuntime.DestinationOf(entryTransitions[0]);
            } else {
                return null;
            }
            if (off == null
                || !states.Any(state => ReferenceEquals(state, off))
                || ToggleConversionRuntime.MotionOf(off) != null) return null;

            // Off → In on the toggle param.
            var offTransitions = ToggleConversionRuntime.TransitionsOf(off);
            if (offTransitions.Count != 1) return null;
            var toIn = offTransitions[0];
            if (ToggleConversionRuntime.IsExit(toIn)
                || ToggleConversionRuntime.HasExitTime(toIn)
                || ToggleConversionRuntime.DurationOf(toIn) != 0) return null;
            var toInConditions = ToggleConversionRuntime.ConditionsOf(toIn);
            if (toInConditions.Length != 1) return null;
            var onCondition = toInConditions[0];
            if (onCondition.mode != AnimatorConditionMode.If) return null;
            var parameter = ToggleConversionRuntime.FindParam(snapshot, onCondition.parameter);
            if (parameter == null || parameter.type != AnimatorControllerParameterType.Bool) return null;
            var inState = ToggleConversionRuntime.DestinationOf(toIn);
            if (inState == null || ReferenceEquals(inState, off)) return null;
            // pure crossfade only
            if (ToggleConversionRuntime.MotionHasValidBinding(
                    snapshot, ToggleConversionRuntime.MotionOf(inState))) return null;

            // In → On unconditionally ("always" param), blending over the fade-in time.
            var inTransitions = ToggleConversionRuntime.TransitionsOf(inState);
            if (inTransitions.Count != 1) return null;
            var toOn = inTransitions[0];
            if (ToggleConversionRuntime.IsExit(toOn) || ToggleConversionRuntime.HasExitTime(toOn)) return null;
            if (!IsAlwaysCondition(snapshot, ToggleConversionRuntime.ConditionsOf(toOn))) return null;
            var onState = ToggleConversionRuntime.DestinationOf(toOn);
            if (onState == null
                || ReferenceEquals(onState, off)
                || ReferenceEquals(onState, inState)) return null;
            var fadeIn = ToggleConversionRuntime.DurationOf(toOn);

            var onMotion = ToggleConversionRuntime.MotionOf(onState);
            if (!ToggleConversionRuntime.MotionIsStatic(onMotion)) return null;
            if (!ToggleConversionRuntime.MotionHasValidBinding(snapshot, onMotion)) return null;
            if (!ToggleConversionRuntime.MotionIsPlainFloat(onMotion)) return null;

            // On → Out when the param drops, blending over the fade-out time.
            var onTransitions = ToggleConversionRuntime.TransitionsOf(onState);
            if (onTransitions.Count != 1) return null;
            var toOut = onTransitions[0];
            if (ToggleConversionRuntime.IsExit(toOut) || ToggleConversionRuntime.HasExitTime(toOut)) return null;
            var toOutConditions = ToggleConversionRuntime.ConditionsOf(toOut);
            if (toOutConditions.Length != 1) return null;
            var negated = ToggleConversionRuntime.Negate(onCondition);
            if (negated == null || !ToggleConversionRuntime.ConditionsEqual(toOutConditions[0], negated.Value)) {
                return null;
            }
            var outState = ToggleConversionRuntime.DestinationOf(toOut);
            if (outState == null
                || ReferenceEquals(outState, off)
                || ReferenceEquals(outState, inState)
                || ReferenceEquals(outState, onState)) return null;
            var fadeOut = ToggleConversionRuntime.DurationOf(toOut);

            // Out → exit unconditionally, instantly.
            if (ToggleConversionRuntime.MotionHasValidBinding(
                    snapshot, ToggleConversionRuntime.MotionOf(outState))) return null;
            var outTransitions = ToggleConversionRuntime.TransitionsOf(outState);
            if (outTransitions.Count != 1) return null;
            var toExit = outTransitions[0];
            if (!ToggleConversionRuntime.IsExit(toExit)
                || ToggleConversionRuntime.HasExitTime(toExit)
                || ToggleConversionRuntime.DurationOf(toExit) != 0) return null;
            if (!IsAlwaysCondition(snapshot, ToggleConversionRuntime.ConditionsOf(toExit))) return null;

            // Default-on variant must point its default state at On.
            if (entryTransitions.Count == 1
                && !ReferenceEquals(ToggleTreeCompat.SmDefaultState.GetValue(machine), onState)) return null;

            if (System.Math.Abs(fadeIn - fadeOut) > 0.001f) {
                skippedAsymmetric++;
                return null;
            }

            if (ToggleConversionRuntime.SharesBindingsWithAnyLayer(snapshot, entry)) return null;

            return new Match {
                Entry = entry,
                OnState = onState,
                OnCondition = onCondition,
                FadeSeconds = fadeIn
            };
        }

        private static bool IsAlwaysCondition(
            ToggleConversionRuntime.Snapshot snapshot,
            AnimatorCondition[] conditions
        ) {
            if (conditions.Length != 1) return false;
            var condition = conditions[0];
            if (condition.mode != AnimatorConditionMode.If) return false;
            if (!ToggleTreeCompat.AlwaysTrueParamName.IsMatch(condition.parameter)) return false;
            var parameter = ToggleConversionRuntime.FindParam(snapshot, condition.parameter);
            return parameter != null
                   && parameter.type == AnimatorControllerParameterType.Bool
                   && parameter.defaultBool;
        }

        private static void Convert(
            ToggleConversionRuntime.Snapshot snapshot,
            Match match,
            object dbt,
            object smoothingService
        ) {
            var layerName = match.Entry.Name;
            var parameter = ToggleConversionRuntime.FindParam(snapshot, match.OnCondition.parameter);
            var def = parameter != null && parameter.defaultBool ? 1f : 0f;

            string blendParam;
            if (match.FadeSeconds <= 0) {
                blendParam = match.OnCondition.parameter;
            } else {
                var target = ToggleTreeCompat.MakeVfaFloat(match.OnCondition.parameter, def);
                var smoothed = ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.Smooth, smoothingService,
                    new object[] {
                        dbt, $"fadeToggle/{layerName}", target, match.FadeSeconds,
                        /* useAcceleration */ false, /* minSupported */ 0f, /* maxSupported */ float.MaxValue
                    });
                blendParam = (string)ToggleTreeCompat.VfaParamName.Invoke(smoothed, null);
            }

            var onFrame = ToggleConversionRuntime.LastFrameOrEmpty(
                ToggleConversionRuntime.MotionOf(match.OnState), $"{layerName} (on)");
            var fadeTree = ToggleTreeCompat.Tree1DCreate.Invoke(
                null, new object[] { $"{layerName} fade", blendParam });
            ToggleTreeCompat.Tree1DAdd.Invoke(fadeTree,
                new object[] { 0f, ToggleTreeCompat.NewEmptyClip($"{layerName} (off)") });
            ToggleTreeCompat.Tree1DAdd.Invoke(fadeTree, new object[] { 1f, onFrame });
            ToggleTreeCompat.DirectAddOne.Invoke(dbt, new object[] { ToggleTreeCompat.TreeToMotion(fadeTree) });
        }
    }
}
