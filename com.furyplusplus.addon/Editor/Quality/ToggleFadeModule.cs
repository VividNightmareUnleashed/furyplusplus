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
    internal sealed class ToggleFadeModule : Module {
        internal static ToggleFadeModule Instance { get; private set; }

        internal ToggleFadeModule() {
            Instance = this;
        }

        internal override string Id => "toggleFadeTrees";
        internal override string DisplayName => "Fade toggles → smoothed blendtree";
        internal override ModuleKind Kind => ModuleKind.Quality;
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
    }

    internal static class ToggleFadePass {
        internal static string LastStats;

        private static readonly Regex AlwaysTrueParam = new Regex(@"^VF_\d+_True$");

        private sealed class Match {
            internal ToggleConversionRuntime.Entry Entry;
            internal AnimatorState OnState;
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
                if (converted.Count > 0 || skippedAsymmetric > 0) {
                    LastStats = $"converted={converted.Count}, skippedAsymmetric={skippedAsymmetric}";
                }
            } catch (System.Exception e) {
                Log.Warn("Fade toggle conversion skipped: " + e.Message);
            }
        }

        private static Match TryMatch(
            ToggleConversionRuntime.Snapshot snapshot,
            ToggleConversionRuntime.Entry entry,
            ref int skippedAsymmetric
        ) {
            var machine = entry.Machine;
            if (machine == null || machine.states.Length != 4) return null;
            if (!ToggleConversionRuntime.PassesCommonLayerGuards(snapshot, entry)) return null;

            var states = machine.states.Select(child => child.state).ToArray();

            // Off is the effective entry state. Default-on toggles make On the default state
            // and add a single unconditional entry transition to Off (ToggleBuilder shape).
            AnimatorState off;
            var entryTransitions = machine.entryTransitions;
            if (entryTransitions.Length == 0) {
                off = machine.defaultState;
            } else if (entryTransitions.Length == 1
                       && entryTransitions[0].conditions.Length == 0
                       && entryTransitions[0].destinationState != null) {
                off = entryTransitions[0].destinationState;
            } else {
                return null;
            }
            if (off == null || !states.Contains(off) || off.motion != null) return null;

            // Off → In on the toggle param.
            if (off.transitions.Length != 1) return null;
            var toIn = off.transitions[0];
            if (toIn.isExit || toIn.hasExitTime || toIn.duration != 0) return null;
            if (toIn.conditions.Length != 1) return null;
            var onCondition = toIn.conditions[0];
            if (onCondition.mode != AnimatorConditionMode.If) return null;
            var parameter = ToggleConversionRuntime.FindParam(snapshot, onCondition.parameter);
            if (parameter == null || parameter.type != AnimatorControllerParameterType.Bool) return null;
            var inState = toIn.destinationState;
            if (inState == null || inState == off) return null;
            if (ToggleConversionRuntime.MotionHasValidBinding(snapshot, inState.motion)) return null; // pure crossfade only

            // In → On unconditionally ("always" param), blending over the fade-in time.
            if (inState.transitions.Length != 1) return null;
            var toOn = inState.transitions[0];
            if (toOn.isExit || toOn.hasExitTime) return null;
            if (!IsAlwaysCondition(snapshot, toOn.conditions)) return null;
            var onState = toOn.destinationState;
            if (onState == null || onState == off || onState == inState) return null;
            var fadeIn = toOn.duration;

            if (!ToggleConversionRuntime.MotionIsStatic(onState.motion)) return null;
            if (!ToggleConversionRuntime.MotionHasValidBinding(snapshot, onState.motion)) return null;
            if (!ToggleConversionRuntime.MotionIsPlainFloat(onState.motion)) return null;

            // On → Out when the param drops, blending over the fade-out time.
            if (onState.transitions.Length != 1) return null;
            var toOut = onState.transitions[0];
            if (toOut.isExit || toOut.hasExitTime) return null;
            if (toOut.conditions.Length != 1) return null;
            var negated = ToggleConversionRuntime.Negate(onCondition);
            if (negated == null || !ToggleConversionRuntime.ConditionsEqual(toOut.conditions[0], negated.Value)) {
                return null;
            }
            var outState = toOut.destinationState;
            if (outState == null || outState == off || outState == inState || outState == onState) return null;
            var fadeOut = toOut.duration;

            // Out → exit unconditionally, instantly.
            if (ToggleConversionRuntime.MotionHasValidBinding(snapshot, outState.motion)) return null;
            if (outState.transitions.Length != 1) return null;
            var toExit = outState.transitions[0];
            if (!toExit.isExit || toExit.hasExitTime || toExit.duration != 0) return null;
            if (!IsAlwaysCondition(snapshot, toExit.conditions)) return null;

            // Default-on variant must point its default state at On.
            if (entryTransitions.Length == 1 && machine.defaultState != onState) return null;

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
            if (!AlwaysTrueParam.IsMatch(condition.parameter)) return false;
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

            var onFrame = ToggleConversionRuntime.LastFrameOrEmpty(match.OnState.motion, $"{layerName} (on)");
            var fadeTree = ToggleTreeCompat.Tree1DCreate.Invoke(
                null, new object[] { $"{layerName} fade", blendParam });
            ToggleTreeCompat.Tree1DAdd.Invoke(fadeTree,
                new object[] { 0f, (Motion)ToggleTreeCompat.NewEmptyClip($"{layerName} (off)") });
            ToggleTreeCompat.Tree1DAdd.Invoke(fadeTree, new object[] { 1f, onFrame });
            ToggleTreeCompat.DirectAddOne.Invoke(dbt, new object[] { ToggleTreeCompat.TreeToMotion(fadeTree) });
        }
    }
}
