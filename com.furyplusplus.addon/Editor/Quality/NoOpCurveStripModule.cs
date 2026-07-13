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
    internal sealed class NoOpCurveStripModule : Module {
        internal static NoOpCurveStripModule Instance { get; private set; }

        internal NoOpCurveStripModule() {
            Instance = this;
        }

        internal override string Id => "noOpCurveStrip";
        internal override string DisplayName => "Strip no-op animation curves";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
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
    }

    internal static class NoOpCurveStripPass {
        internal static string LastStats;

        private static MethodInfo getAllUsedControllers;
        private static Type clipsIteratorType;
        private static MethodInfo clipsFrom;
        private static MethodInfo getAllCurves;
        private static MethodInfo setCurves;
        private static MethodInfo isProxyClip;
        private static MethodInfo getDefaultClip;
        private static PropertyInfo curveIsFloat;
        private static PropertyInfo curveFloatCurve;
        private static Type curveTupleType;

        internal static void Resolve() {
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            getAllUsedControllers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetAllUsedControllers",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetAllUsedControllers()");

            clipsIteratorType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips"), "VF.Utils.AnimatorIterator+Clips");
            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"), "VF.Utils.Controller.VFController");
            clipsFrom = ReflectionUtils.Demand(
                clipsIteratorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(method => method.Name == "From"
                                               && method.GetParameters().Length == 1
                                               && method.GetParameters()[0].ParameterType == vfControllerType),
                "AnimatorIterator.Clips.From(VFController)");

            var clipExtType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions"), "VF.Utils.AnimationClipExtensions");
            getAllCurves = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "GetAllCurves",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.GetAllCurves(clip)");
            setCurves = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "SetCurves",
                    method => method.GetParameters().Length == 2),
                "AnimationClipExtensions.SetCurves(clip, curves)");
            isProxyClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "IsProxyClip",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.IsProxyClip(clip)");

            var fixWdType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.FixWriteDefaultsService"), "VF.Service.FixWriteDefaultsService");
            getDefaultClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(fixWdType, "GetDefaultClip",
                    method => method.GetParameters().Length == 0),
                "FixWriteDefaultsService.GetDefaultClip()");

            var curveType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve"), "VF.Utils.FloatOrObjectCurve");
            curveIsFloat = ReflectionUtils.Demand(curveType.GetProperty("IsFloat"), "FloatOrObjectCurve.IsFloat");
            curveFloatCurve = ReflectionUtils.Demand(curveType.GetProperty("FloatCurve"), "FloatOrObjectCurve.FloatCurve");

            curveTupleType = ReflectionUtils.Demand(
                getAllCurves.ReturnType.GetElementType(), "(EditorCurveBinding, FloatOrObjectCurve)");
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
            var managers = (IEnumerable)getAllUsedControllers.Invoke(controllersService, null);
            var iterator = Activator.CreateInstance(clipsIteratorType);
            foreach (var manager in managers) {
                foreach (var clip in (IEnumerable)clipsFrom.Invoke(iterator, new[] { manager })) {
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
                var curves = (Array)getAllCurves.Invoke(null, new object[] { clip });
                foreach (var entry in curves) {
                    var binding = (EditorCurveBinding)curveTupleType.GetField("Item1").GetValue(entry);
                    var curve = curveTupleType.GetField("Item2").GetValue(entry);

                    // AAPs are parameters, not properties — never touch.
                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    if (curve == null || !(bool)curveIsFloat.GetValue(curve)) {
                        blockedBindings.Add(binding);
                        continue;
                    }
                    var floatCurve = curveFloatCurve.GetValue(curve) as AnimationCurve;
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
                if ((bool)isProxyClip.Invoke(null, new object[] { clip })) continue;
                var removals = group.ToList();
                if (removals.Count == 0) continue;

                var tuples = Array.CreateInstance(curveTupleType, removals.Count);
                for (var i = 0; i < removals.Count; i++) {
                    tuples.SetValue(Activator.CreateInstance(curveTupleType, removals[i].Binding, null), i);
                    if (examples.Count < 8) {
                        examples.Add($"{removals[i].Binding.path}/{removals[i].Binding.propertyName}={removals[i].Value}");
                    }
                }
                setCurves.Invoke(null, new object[] { clip, tuples });
                strippedCurves += removals.Count;
                touchedClips++;
            }

            if (strippedCurves > 0) {
                Log.Info($"Stripped {strippedCurves} no-op curve(s) from {touchedClips} clip(s) " +
                         $"(all writers were resting-value constants). e.g. {string.Join("; ", examples)}");
            }
            LastStats = strippedCurves == 0 ? null : $"curves={strippedCurves} clips={touchedClips}";
        }

        private static bool IsConstant(AnimationCurve curve, out float value) {
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

        private static bool ValuesMatch(EditorCurveBinding binding, float curveValue, float restValue) {
            if (curveValue.Equals(restValue)) return true;
            // Blendshape weights round-trip through floats; VRCFury itself compares them
            // approximately (BlendshapeOptimizerBuilder does the same).
            return binding.propertyName.StartsWith("blendShape.")
                   && Mathf.Approximately(curveValue, restValue);
        }
    }
}
