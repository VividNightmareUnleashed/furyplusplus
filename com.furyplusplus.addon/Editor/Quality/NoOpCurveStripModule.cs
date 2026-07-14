using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Strips animation curves that can never change anything: constant float curves whose
     * value equals the avatar's rest value for that binding, when EVERY writer of that
     * binding (across all used controllers) is such a no-op. Under layer-override semantics
     * removing all default-writing curves leaves the property at its default via WD/the
     * defaults layer — indistinguishable at runtime, but the curves stop being evaluated
     * and emptied clips/layers get swept by VRCFury's own CleanupEmptyLayers right after.
     *
     * Runs mid-build (before FeatureOrder.CleanupEmptyLayers) through VRCFury's clip
     * ext-db — post-save clip mutation is unsafe (original-clip aliasing). Conservative on
     * purpose: float curves only; a curve counts as constant only when every key has the
     * same value and zero tangents; unknown rest values block stripping.
     */
    internal sealed class NoOpCurveStripModule : Module<NoOpCurveStripModule> {
        internal override string Id => "noOpCurveStrip";
        internal override string DisplayName => "Strip no-op animation curves";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Animation clips";
        internal override string Description =>
            "Removes curves that only ever write a property's resting value — fewer " +
            "always-evaluated writes after blendtree conversion, smaller clips.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            NoOpCurveStripPass.Resolve();
            BuildPhaseHooks.RegisterBefore("CleanupEmptyLayers", Id, _ => NoOpCurveStripPass.Run());
        }

        internal override string ReportStats() {
            return NoOpCurveStripPass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return NoOpCurveStripPass.LastStrippedCurves > 0
                ? ($"{N(NoOpCurveStripPass.LastStrippedCurves)} curves stripped last bake",
                    NoOpCurveStripPass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class NoOpCurveStripPass {
        internal static string LastStats;
        internal static int LastStrippedCurves;

        private static MethodInfo getDefaultClip;

        internal static void Resolve() {
            ClipCurveCompat.DemandCore();
            ReflectionUtils.Demand(ClipCurveCompat.ClipsFromController, "AnimatorIterator.Clips.From(VFController)");
            ReflectionUtils.Demand(ClipCurveCompat.SetCurves, "AnimationClipExtensions.SetCurves(clip, curves)");

            var fixWdType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.FixWriteDefaultsService"), "VF.Service.FixWriteDefaultsService");
            getDefaultClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(fixWdType, "GetDefaultClip",
                    method => method.GetParameters().Length == 0),
                "FixWriteDefaultsService.GetDefaultClip()");
        }

        internal static void Run() {
            var avatarRoot = BuildPhaseHooks.CurrentAvatarRoot;
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            if (avatarRoot == null || controllersService == null || fixWd == null) {
                return; // no injector context this run — do nothing
            }
            var defaultClip = getDefaultClip.Invoke(fixWd, null) as AnimationClip;

            // Collect every clip of every used controller once.
            var clips = new HashSet<AnimationClip>();
            var managers = (IEnumerable)ClipCurveCompat.GetAllUsedControllers.Invoke(controllersService, null);
            foreach (var manager in managers) {
                foreach (var clip in ClipCurveCompat.ClipsFrom(manager)) {
                    if (clip is AnimationClip animationClip) clips.Add(animationClip);
                }
            }

            // Pass 1: classify every (binding, curve). A binding is strippable only if EVERY
            // writer of it, in any clip, is a constant float curve equal to the rest value.
            var blockedBindings = new HashSet<EditorCurveBinding>();
            var candidates = new List<(AnimationClip Clip, EditorCurveBinding Binding, float Value)>();
            var restCache = new Dictionary<EditorCurveBinding, (bool Known, float Value)>();

            (bool Known, float Value) RestOf(EditorCurveBinding binding) {
                if (restCache.TryGetValue(binding, out var cached)) return cached;
                var known = AnimationUtility.GetFloatValue(avatarRoot, binding, out var value);
                var result = (known, value);
                restCache[binding] = result;
                return result;
            }

            foreach (var clip in clips) {
                var curves = ClipCurveCompat.AllCurvesOf(clip);
                foreach (var entry in curves) {
                    var binding = ClipCurveCompat.TupleBinding(entry);
                    var curve = ClipCurveCompat.TupleCurve(entry);

                    // AAPs are parameters, not properties — never touch.
                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    if (curve == null || !ClipCurveCompat.IsFloat(curve)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    var floatCurve = ClipCurveCompat.FloatCurveOf(curve);
                    if (floatCurve == null || !IsConstant(floatCurve, out var value)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    var rest = RestOf(binding);
                    if (!rest.Known || !ValuesMatch(binding, value, rest.Value)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    candidates.Add((clip, binding, value));
                }
            }

            // Pass 2: strip surviving candidates per clip (never proxies or the defaults clip;
            // untouched user-clip copies are only mutated when something is actually removed,
            // preserving VRCFury's original-clip aliasing at save).
            var byClip = candidates
                .Where(candidate => !blockedBindings.Contains(candidate.Binding))
                .Where(candidate => candidate.Clip != defaultClip)
                .GroupBy(candidate => candidate.Clip);

            var strippedCurves = 0;
            var touchedClips = 0;
            var examples = new List<string>();
            foreach (var group in byClip) {
                var clip = group.Key;
                if (ClipCurveCompat.IsProxyClip(clip)) continue;
                var removals = group.ToList();
                if (removals.Count == 0) continue;

                var tuples = Array.CreateInstance(ClipCurveCompat.CurveTupleType, removals.Count);
                for (var i = 0; i < removals.Count; i++) {
                    tuples.SetValue(ClipCurveCompat.CreateTuple(removals[i].Binding, null), i);
                    if (examples.Count < 8) {
                        examples.Add($"{removals[i].Binding.path}/{removals[i].Binding.propertyName}={removals[i].Value}");
                    }
                }
                ClipCurveCompat.SetCurves.Invoke(null, new object[] { clip, tuples });
                strippedCurves += removals.Count;
                touchedClips++;
            }

            if (strippedCurves > 0) {
                Log.Info($"Stripped {strippedCurves} no-op curve(s) from {touchedClips} clip(s) " +
                         $"(all writers were resting-value constants). e.g. {string.Join("; ", examples)}");
            }
            LastStrippedCurves = strippedCurves;
            LastStats = strippedCurves == 0 ? null : $"curves={strippedCurves} clips={touchedClips}";
        }

        /**
         * The shared "no-op write at rest" doctrine — OffSideEliminationPatch applies the
         * same rules to a candidate off clip; both modules' safety arguments depend on this
         * single definition.
         */
        internal static bool IsConstant(AnimationCurve curve, out float value) {
            value = 0;
            var keys = curve.keys;
            if (keys.Length == 0) return false;
            value = keys[0].value;
            foreach (var key in keys) {
                if (!key.value.Equals(value)) return false;
            }
            if (keys.Length > 1) {
                // Equal endpoints with nonzero tangents can still overshoot between keys.
                foreach (var key in keys) {
                    if (key.inTangent != 0 || key.outTangent != 0) return false;
                }
            }
            return true;
        }

        internal static bool ValuesMatch(EditorCurveBinding binding, float curveValue, float restValue) {
            if (curveValue.Equals(restValue)) return true;
            // Blendshape weights round-trip through floats; VRCFury itself compares them
            // approximately (BlendshapeOptimizerBuilder does the same).
            return binding.propertyName.StartsWith("blendShape.")
                   && Mathf.Approximately(curveValue, restValue);
        }
    }
}
