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
            ReflectionUtils.Demand(ClipCurveCompat.ClipSetCurves, "VFClip.SetCurves(curves)");
            ReflectionUtils.Demand(ClipCurveCompat.BindingTryGetCurrentFloat,
                "VFBinding.TryGetCurrentFloat(root, out value)");

            ToggleTreeCompat.EnsureResolved();
            getDefaultClip = ReflectionUtils.Demand(
                ToggleTreeCompat.GetDefaultClip, "FixWriteDefaultsService.GetDefaultClip()");
        }

        internal static void Run() {
            var avatarRoot = BuildPhaseHooks.CurrentAvatarRoot;
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            var fixWd = BuildPhaseHooks.GetService("VF.Service.FixWriteDefaultsService");
            if (avatarRoot == null || controllersService == null || fixWd == null) {
                return; // no injector context this run — do nothing
            }
            var vfAvatarRoot = ClipCurveCompat.WrapGameObject(avatarRoot);
            if (vfAvatarRoot == null) return;
            var defaultClip = getDefaultClip.Invoke(fixWd, null);

            // Collect every clip of every used controller once. Clips are in-memory VFClip
            // objects, so identity is reference identity.
            var clips = new HashSet<object>();
            foreach (var manager in ClipCurveCompat.UsedControllers(controllersService)) {
                foreach (var clip in ClipCurveCompat.ClipsFrom(manager)) {
                    if (clip != null) clips.Add(clip);
                }
            }

            // Pass 1: classify every (binding, curve). A binding is strippable only if EVERY
            // writer of it, in any clip, is a constant float curve equal to the rest value.
            // Bindings are boxed VFBinding structs — value-equal across clips by construction.
            var blockedBindings = new HashSet<object>();
            var candidates = new List<(object Clip, object Binding, float Value)>();
            var restCache = new Dictionary<object, (bool Known, float Value)>();

            (bool Known, float Value) RestOf(object binding) {
                if (restCache.TryGetValue(binding, out var cached)) return cached;
                var known = ClipCurveCompat.TryGetRestValue(binding, vfAvatarRoot, out var value);
                var result = (known, value);
                restCache[binding] = result;
                return result;
            }

            foreach (var clip in clips) {
                var curves = ClipCurveCompat.AllCurvesOf(clip);
                foreach (var entry in curves) {
                    var binding = ClipCurveCompat.TupleBinding(entry);
                    var curve = ClipCurveCompat.TupleCurve(entry);

                    // AAPs and humanoid muscles are animator-stream values, not scene
                    // properties — they have no resting value to fall back to. Never touch.
                    if (ClipCurveCompat.IsAnimatorBinding(binding)) {
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
                    if (!rest.Known
                        || !ValuesMatch(ClipCurveCompat.PropertyNameOf(binding), value, rest.Value)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    candidates.Add((clip, binding, value));
                }
            }

            // Pass 2: strip surviving candidates per clip (never proxies or the defaults clip).
            // VFClip.SetCurves flags the clip as changed-from-source, so a clip that keeps all
            // its curves is never touched and stays eligible to alias its original user asset.
            var byClip = candidates
                .Where(candidate => !blockedBindings.Contains(candidate.Binding))
                .Where(candidate => !ReferenceEquals(candidate.Clip, defaultClip))
                .GroupBy(candidate => candidate.Clip);

            var strippedCurves = 0;
            var touchedClips = 0;
            var examples = new List<string>();
            foreach (var group in byClip) {
                var clip = group.Key;
                if (ClipCurveCompat.IsProxyClip(clip)) continue;
                var removals = group.ToList();
                if (removals.Count == 0) continue;

                var batch = ClipCurveCompat.NewCurveBatch();
                foreach (var removal in removals) {
                    // A null curve is how VFClip.SetCurves spells "remove this binding".
                    ClipCurveCompat.AddToBatch(batch, removal.Binding, null);
                    if (examples.Count < 8) {
                        examples.Add($"{ClipCurveCompat.DebugPathOf(removal.Binding)}/" +
                                     $"{ClipCurveCompat.PropertyNameOf(removal.Binding)}={removal.Value}");
                    }
                }
                ClipCurveCompat.SetCurves(clip, batch);
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

        internal static bool ValuesMatch(string propertyName, float curveValue, float restValue) {
            if (curveValue.Equals(restValue)) return true;
            // Blendshape weights round-trip through floats; VRCFury itself compares them
            // approximately (BlendshapeOptimizerBuilder does the same).
            return propertyName != null
                   && propertyName.StartsWith("blendShape.")
                   && Mathf.Approximately(curveValue, restValue);
        }
    }
}
