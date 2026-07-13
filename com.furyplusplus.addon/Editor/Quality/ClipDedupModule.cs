using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Controller-wide dedup of VRCFury-generated clips: identical curve sets + settings
     * collapse to one shared instance before SaveAssets, so the saved controller references
     * (and the upload ships) each unique clip once. VRCFury's own merging only operates
     * within a single direct blendtree; identical generated clips across layers/states stay
     * separate without this.
     *
     * Conservative: only clips VRCFury generated or changed (GetUseOriginalUserClip == null)
     * and non-proxy clips participate; hash covers every curve key (times, values, tangents,
     * weights) plus the full AnimationClipSettings, so differing loop/length can never merge.
     * First occurrence in controller/layer order wins.
     */
    internal sealed class ClipDedupModule : Module {
        internal static ClipDedupModule Instance { get; private set; }

        internal ClipDedupModule() {
            Instance = this;
        }

        internal override string Id => "clipDedup";
        internal override string DisplayName => "Deduplicate generated clips (controller-wide)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Points identical VRCFury-generated animation clips at one shared instance " +
            "across all layers and blendtrees before assets are saved.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ClipDedupPass.Resolve();
            BuildPhaseHooks.RegisterBefore("SaveAssets", Id, _ => ClipDedupPass.Run());
        }

        internal override string ReportStats() {
            return ClipDedupPass.LastStats;
        }
    }

    internal static class ClipDedupPass {
        internal static string LastStats;

        private static MethodInfo getAllUsedControllers;
        private static FieldInfo vfControllerCtrlField;
        private static MethodInfo getAllCurves;
        private static MethodInfo isProxyClip;
        private static MethodInfo getUseOriginalUserClip;
        private static Type curveTupleType;
        private static PropertyInfo curveIsFloat;
        private static PropertyInfo curveFloatCurve;
        private static PropertyInfo curveObjectCurve;

        internal static void Resolve() {
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            getAllUsedControllers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetAllUsedControllers",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetAllUsedControllers()");
            vfControllerCtrlField = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController")
                    ?.GetField("ctrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                "VFController.ctrl");

            var clipExtType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions"), "VF.Utils.AnimationClipExtensions");
            getAllCurves = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "GetAllCurves",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.GetAllCurves(clip)");
            isProxyClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "IsProxyClip",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.IsProxyClip(clip)");
            getUseOriginalUserClip = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(clipExtType, "GetUseOriginalUserClip",
                    method => method.GetParameters().Length == 1),
                "AnimationClipExtensions.GetUseOriginalUserClip(clip)");

            curveTupleType = ReflectionUtils.Demand(
                getAllCurves.ReturnType.GetElementType(), "(EditorCurveBinding, FloatOrObjectCurve)");
            var curveType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve"), "VF.Utils.FloatOrObjectCurve");
            curveIsFloat = ReflectionUtils.Demand(curveType.GetProperty("IsFloat"), "FloatOrObjectCurve.IsFloat");
            curveFloatCurve = ReflectionUtils.Demand(curveType.GetProperty("FloatCurve"), "FloatOrObjectCurve.FloatCurve");
            curveObjectCurve = ReflectionUtils.Demand(curveType.GetProperty("ObjectCurve"), "FloatOrObjectCurve.ObjectCurve");
        }

        internal static void Run() {
            var controllersService = BuildPhaseHooks.GetService("VF.Service.ControllersService");
            if (controllersService == null) return;

            // Discover canonical clips in deterministic controller/layer order.
            var canonicalByHash = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            var replacement = new Dictionary<AnimationClip, AnimationClip>();
            var managers = ((IEnumerable)getAllUsedControllers.Invoke(controllersService, null))
                .Cast<object>().ToList();
            var controllers = managers
                .Select(manager => vfControllerCtrlField.GetValue(manager) as AnimatorController)
                .Where(controller => controller != null)
                .ToList();

            void Discover(AnimationClip clip) {
                if (clip == null || replacement.ContainsKey(clip)) return;
                if ((bool)isProxyClip.Invoke(null, new object[] { clip })) return;
                if (getUseOriginalUserClip.Invoke(null, new object[] { clip }) != null) return;
                string hash;
                try {
                    hash = HashClip(clip);
                } catch {
                    return; // unhashable clip: leave it alone
                }
                if (canonicalByHash.TryGetValue(hash, out var canonical)) {
                    if (!ReferenceEquals(canonical, clip)) replacement[clip] = canonical;
                } else {
                    canonicalByHash[hash] = clip;
                }
            }

            void WalkMotionDiscover(Motion motion, HashSet<Motion> seen) {
                if (motion == null || !seen.Add(motion)) return;
                if (motion is AnimationClip clip) {
                    Discover(clip);
                } else if (motion is BlendTree tree) {
                    foreach (var child in tree.children) WalkMotionDiscover(child.motion, seen);
                }
            }

            var seenMotions = new HashSet<Motion>();
            foreach (var controller in controllers) {
                foreach (var layer in controller.layers) {
                    WalkStates(layer.stateMachine, state => WalkMotionDiscover(state.motion, seenMotions));
                }
            }

            if (replacement.Count == 0) {
                LastStats = null;
                return;
            }

            // Repoint every reference (public animator API only).
            var repointed = 0;
            foreach (var controller in controllers) {
                foreach (var layer in controller.layers) {
                    WalkStates(layer.stateMachine, state => {
                        if (state.motion is AnimationClip clip && replacement.TryGetValue(clip, out var canon)) {
                            state.motion = canon;
                            repointed++;
                        } else if (state.motion is BlendTree tree) {
                            repointed += RepointTree(tree, replacement, new HashSet<BlendTree>());
                        }
                    });
                }
            }

            Log.Info($"Deduplicated {replacement.Count} identical generated clip(s) " +
                     $"({repointed} reference(s) repointed).");
            LastStats = $"duplicates={replacement.Count} repointed={repointed}";
        }

        private static void WalkStates(AnimatorStateMachine machine, Action<AnimatorState> visit) {
            if (machine == null) return;
            foreach (var child in machine.states) {
                if (child.state != null) visit(child.state);
            }
            foreach (var child in machine.stateMachines) {
                WalkStates(child.stateMachine, visit);
            }
        }

        private static int RepointTree(BlendTree tree, Dictionary<AnimationClip, AnimationClip> replacement, HashSet<BlendTree> seen) {
            if (!seen.Add(tree)) return 0;
            var repointed = 0;
            var children = tree.children;
            var changed = false;
            for (var i = 0; i < children.Length; i++) {
                if (children[i].motion is AnimationClip clip && replacement.TryGetValue(clip, out var canon)) {
                    children[i].motion = canon;
                    changed = true;
                    repointed++;
                } else if (children[i].motion is BlendTree childTree) {
                    repointed += RepointTree(childTree, replacement, seen);
                }
            }
            if (changed) tree.children = children;
            return repointed;
        }

        private static string HashClip(AnimationClip clip) {
            var builder = new StringBuilder();

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            foreach (var field in settings.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(field => field.Name, StringComparer.Ordinal)) {
                var value = field.GetValue(settings);
                builder.Append(field.Name).Append('=')
                    .Append(value is UnityEngine.Object obj ? obj.GetInstanceID().ToString() : value?.ToString())
                    .Append(';');
            }
            builder.Append("fr=").Append(clip.frameRate).Append('|');

            var entries = new List<string>();
            var curves = (Array)getAllCurves.Invoke(null, new object[] { clip });
            foreach (var entry in curves) {
                var binding = (EditorCurveBinding)curveTupleType.GetField("Item1").GetValue(entry);
                var curve = curveTupleType.GetField("Item2").GetValue(entry);
                var sb = new StringBuilder();
                sb.Append(binding.path).Append('')
                  .Append(binding.type?.FullName).Append('')
                  .Append(binding.propertyName).Append('');
                if (curve == null) {
                    sb.Append("null");
                } else if ((bool)curveIsFloat.GetValue(curve)) {
                    var floatCurve = (AnimationCurve)curveFloatCurve.GetValue(curve);
                    sb.Append("F:").Append(floatCurve.preWrapMode).Append(',').Append(floatCurve.postWrapMode);
                    foreach (var key in floatCurve.keys) {
                        sb.Append('(').Append(key.time.ToString("R")).Append(',')
                          .Append(key.value.ToString("R")).Append(',')
                          .Append(key.inTangent.ToString("R")).Append(',')
                          .Append(key.outTangent.ToString("R")).Append(',')
                          .Append(key.inWeight.ToString("R")).Append(',')
                          .Append(key.outWeight.ToString("R")).Append(',')
                          .Append((int)key.weightedMode).Append(')');
                    }
                } else {
                    var objectCurve = (ObjectReferenceKeyframe[])curveObjectCurve.GetValue(curve);
                    sb.Append("O:");
                    if (objectCurve != null) {
                        foreach (var key in objectCurve) {
                            sb.Append('(').Append(key.time.ToString("R")).Append(',')
                              .Append(key.value == null ? 0 : key.value.GetInstanceID()).Append(')');
                        }
                    }
                }
                entries.Add(sb.ToString());
            }
            entries.Sort(StringComparer.Ordinal);
            foreach (var entry in entries) builder.Append(entry).Append('');
            return builder.ToString();
        }
    }
}
