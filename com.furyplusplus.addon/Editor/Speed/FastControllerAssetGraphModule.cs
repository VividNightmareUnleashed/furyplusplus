using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace FuryPlusPlus {
    /**
     * Replaces SaveAssetsSession's SerializedObject walk for AnimatorControllers with a
     * traversal of Unity's public controller graph. This reaches the same states, state
     * machines, transitions, behaviours, motions and masks without inspecting every
     * serialized property on every node.
     */
    internal sealed class FastControllerAssetGraphModule : Module {
        internal static FastControllerAssetGraphModule Instance { get; private set; }

        internal static readonly ModuleOption DeduplicateClipsOption = new ModuleOption(
            "deduplicateClips",
            "Deduplicate generated clips at save",
            true
        );

        private static readonly ModuleOption[] AllOptions = {
            DeduplicateClipsOption
        };

        internal FastControllerAssetGraphModule() {
            Instance = this;
        }

        internal override string Id => "fastControllerAssetGraph";
        internal override string DisplayName => "Fast controller asset graph";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Walks Unity's public controller graph to find unsaved children instead of VRCFury's SerializedObject scan.";
        internal override IReadOnlyList<ModuleOption> Options => AllOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            FastControllerAssetGraphPatch.Install(harmony, compat);
        }

        internal override string ReportStats() {
            var stats = FastControllerAssetGraphPatch.LastStats;
            return string.IsNullOrEmpty(stats) || stats == "none" ? null : stats;
        }
    }

    internal static class FastControllerAssetGraphPatch {
        private static MethodInfo getUseOriginalClip;
        private static MethodInfo getClipExt;
        private static MethodInfo finalizeClip;
        private static MethodInfo rewriteInternals;
        private static FieldInfo extCurves;
        private static FieldInfo extOriginalSourceClip;
        private static PropertyInfo curveIsFloat;
        private static PropertyInfo curveFloatCurve;
        private static PropertyInfo curveObjectCurve;
        private static PropertyInfo[] clipSettingsProperties;
        [ThreadStatic] private static HashSet<Object> savedOrScheduled;
        [ThreadStatic] private static long lastStatsTicks;
        internal static string LastStats { get; private set; } = "none";

        internal static void Install(Harmony harmony, VrcfuryCompat targets) {
            SaveAssetsCompat.EnsureResolved();
            var sessionType = ReflectionUtils.FindType("VF.Utils.SaveAssetsSession");
            var getUnsavedChildren = ReflectionUtils.FindUniqueMethod(
                sessionType,
                "GetUnsavedChildren",
                method => {
                    var parameters = method.GetParameters();
                    return parameters.Length == 3
                           && parameters[0].ParameterType == typeof(Object)
                           && parameters[1].ParameterType == typeof(bool)
                           && parameters[2].ParameterType == typeof(bool);
                }
            );

            var clipExtensions = ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions");
            getUseOriginalClip = ReflectionUtils.FindUniqueMethod(
                clipExtensions,
                "GetUseOriginalUserClip",
                method => method.ReturnType == typeof(AnimationClip) && method.GetParameters().Length == 1
            );
            getClipExt = ReflectionUtils.FindUniqueMethod(
                clipExtensions,
                "GetExt",
                method => method.GetParameters().Length == 1
                          && method.GetParameters()[0].ParameterType == typeof(AnimationClip)
            );
            finalizeClip = ReflectionUtils.FindUniqueMethod(
                clipExtensions,
                "FinalizeAsset",
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 2
            );

            var clipExtType = getClipExt?.ReturnType;
            extCurves = clipExtType?.GetField(
                "curves",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            extOriginalSourceClip = clipExtType?.GetField(
                "originalSourceClip",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            var curveType = ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve");
            curveIsFloat = curveType?.GetProperty(
                "IsFloat",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            curveFloatCurve = curveType?.GetProperty(
                "FloatCurve",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            curveObjectCurve = curveType?.GetProperty(
                "ObjectCurve",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            var mutableManager = ReflectionUtils.FindType("VF.Utils.MutableManager");
            rewriteInternals = ReflectionUtils.FindUniqueMethod(
                mutableManager,
                "RewriteInternals",
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 2
            );

            var saveAssetsRun = SaveAssetsCompat.SaveAssetsRun;
            var assetDatabaseType = ReflectionUtils.FindType("VF.Utils.VRCFuryAssetDatabase");
            var saveMethods = assetDatabaseType?
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => (method.Name == "SaveAsset" || method.Name == "AttachAsset")
                                 && method.GetParameters().Length >= 2
                                 && method.GetParameters()[0].ParameterType == typeof(Object))
                .ToArray() ?? Array.Empty<MethodInfo>();

            if (getUnsavedChildren == null || SaveAssetsCompat.FactoryDidCreate == null || getUseOriginalClip == null
                                           || getClipExt == null || finalizeClip == null
                                           || extCurves == null || extOriginalSourceClip == null
                                           || curveIsFloat == null || curveFloatCurve == null
                                           || curveObjectCurve == null || rewriteInternals == null
                                           || saveAssetsRun == null || saveMethods.Length < 3) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                getUnsavedChildren,
                prefix: new HarmonyMethod(typeof(FastControllerAssetGraphPatch), nameof(Prefix))
            );
            harmony.Patch(
                saveAssetsRun,
                prefix: new HarmonyMethod(typeof(FastControllerAssetGraphPatch), nameof(BeginSaveRun)),
                finalizer: new HarmonyMethod(typeof(FastControllerAssetGraphPatch), nameof(EndSaveRun))
            );
            foreach (var method in saveMethods) {
                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(FastControllerAssetGraphPatch), nameof(AssetSaved))
                );
            }
        }

        private static void BeginSaveRun() {
            savedOrScheduled = new HashSet<Object>();
            lastStatsTicks = 0;
        }

        private static Exception EndSaveRun(Exception __exception) {
            savedOrScheduled = null;
            return __exception;
        }

        private static void AssetSaved(Object __0) {
            if (__0 != null) savedOrScheduled?.Add(__0);
        }

        private static bool Prefix(
            Object obj,
            bool recurse,
            bool reuseOriginalClips,
            ref IList<Object> __result
        ) {
            if (FastControllerAssetGraphModule.Instance?.Enabled != true || !recurse) {
                return true;
            }

            try {
                if (obj is AnimatorController controller) {
                    __result = Collect(controller, reuseOriginalClips);
                    return false;
                }
                if (obj is Material material) {
                    __result = CollectMaterialChildren(material);
                    return false;
                }
                return true;
            } catch (Exception e) {
                Log.Warn("Fast asset graph fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        private static IList<Object> CollectMaterialChildren(Material material) {
            var output = new List<Object>();
            var seen = new HashSet<Object>();
            foreach (var property in material.GetTexturePropertyNames()) {
                var texture = material.GetTexture(property);
                if (texture == null || !seen.Add(texture)
                                    || !SaveAssetsCompat.DidCreate(texture)) continue;
                if (savedOrScheduled != null && savedOrScheduled.Contains(texture)) continue;
                if (!PatchUtils.IsPersisted(texture)) output.Add(texture);
            }
            return output;
        }

        private static IList<Object> Collect(AnimatorController controller, bool reuseOriginalClips) {
            // Snapshot per controller: EditorPrefs reads are native registry hits, and the
            // tick capture below is pure overhead unless detailed profiling is enabled.
            var deduplicateClips = Settings.IsOptionEnabled(
                FastControllerAssetGraphModule.Instance,
                FastControllerAssetGraphModule.DeduplicateClipsOption
            );
            var detailed = Settings.DetailedProfiling;
            long Timestamp() => detailed ? Stopwatch.GetTimestamp() : 0L;

            long includeTicks = 0;
            long pushTicks = 0;
            long dependencyTicks = 0;
            long rewriteTicks = 0;
            long settingsTicks = 0;
            long didCreateTicks = 0;
            long originalClipTicks = 0;
            long finalizeClipTicks = 0;
            long deduplicateClipTicks = 0;
            var clipCount = 0;
            var deduplicatedClips = 0;
            var output = new List<Object>();
            var visited = new HashSet<Object>();
            var stack = new Stack<Object>();
            var clipReplacements = new Dictionary<Object, Object>();
            var generatedClipByContent = new Dictionary<string, AnimationClip>();
            var nativeDependencyRoots = new List<Object>();
            var nativeDependencies = new HashSet<Object>();
            var clipDependencies = new Dictionary<AnimationClip, List<Object>>();
            var rootPath = AssetDatabase.GetAssetPath(controller);
            if (savedOrScheduled != null && !string.IsNullOrEmpty(rootPath)) {
                // Load the controller file's existing subassets once. Querying
                // GetAssetPath separately for thousands of graph nodes costs seconds.
                foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(rootPath)) {
                    if (existing != null) savedOrScheduled.Add(existing);
                }
                // The root itself must still be traversed so newly appended graph nodes
                // can be discovered during a later preprocessor hook.
                savedOrScheduled.Remove(controller);
            }
            stack.Push(controller);

            bool Include(Object current) {
                if (current == controller) return true;
                var timed = Timestamp();
                var created = SaveAssetsCompat.DidCreate(current);
                didCreateTicks += Timestamp() - timed;
                if (!created) return false;

                var known = savedOrScheduled;
                // Persisted controller nodes still need their outgoing references
                // traversed: a later preprocessor hook can append a fresh child to an
                // already-saved state, behaviour or blend tree.
                if (known != null && known.Contains(current)) return true;

                // Outside a SaveAssets run retain VRCFury's normal persistence check.
                // During a run, saved/scheduled objects are tracked in-memory and an
                // adopted controller's existing subassets were loaded above in bulk.
                if (known == null && PatchUtils.IsPersisted(current)) {
                    return true;
                }

                if (current is AnimationClip clip) {
                    clipCount++;
                    if (reuseOriginalClips) {
                        timed = Timestamp();
                        var original = ReflectionUtils.InvokeUnwrapped(
                            getUseOriginalClip,
                            null,
                            new object[] { clip }
                        ) as AnimationClip;
                        originalClipTicks += Timestamp() - timed;
                        if (original != null) {
                            clipReplacements[clip] = original;
                            return false;
                        }
                    }
                    if (deduplicateClips) {
                        timed = Timestamp();
                        var contentKey = GetGeneratedClipContentKey(clip);
                        deduplicateClipTicks += Timestamp() - timed;
                        if (contentKey != null) {
                            if (generatedClipByContent.TryGetValue(contentKey, out var canonical)) {
                                clipReplacements[clip] = canonical;
                                deduplicatedClips++;
                                return false;
                            }
                            generatedClipByContent.Add(contentKey, clip);
                        }
                    }
                    timed = Timestamp();
                    ReflectionUtils.InvokeUnwrapped(finalizeClip, null, new object[] { clip, true });
                    finalizeClipTicks += Timestamp() - timed;
                }
                known?.Add(current);
                output.Add(current);
                return true;
            }

            while (stack.Count > 0) {
                var current = stack.Pop();
                if (current == null || !visited.Add(current)) continue;
                var started = Timestamp();
                var shouldTraverse = Include(current);
                includeTicks += Timestamp() - started;
                if (!shouldTraverse) continue;
                started = Timestamp();
                PushChildren(current, stack, nativeDependencyRoots, clipDependencies);
                pushTicks += Timestamp() - started;
            }

            // CollectDependencies is recursive. One batched native traversal handles the
            // arbitrary serialized fields on all StateMachineBehaviours. AnimationClip
            // dependencies are enumerated precisely through AnimationUtility above.
            if (nativeDependencyRoots.Count > 0) {
                var started = Timestamp();
                // PushChildren runs once per visited behaviour, so the roots are unique.
                foreach (var dependency in EditorUtility.CollectDependencies(
                             nativeDependencyRoots.ToArray()
                         )) {
                    if (dependency != null) nativeDependencies.Add(dependency);
                    if (dependency == null || !visited.Add(dependency)) continue;
                    Include(dependency);
                }
                dependencyTicks += Timestamp() - started;
            }

            if (clipReplacements.Count > 0) {
                var started = Timestamp();
                RewriteMotions(output, clipReplacements);
                RewriteClipReferences(
                    output.OfType<AnimationClip>().Where(clip =>
                        clipDependencies.TryGetValue(clip, out var dependencies)
                        && dependencies.Any(clipReplacements.ContainsKey)
                    ),
                    clipReplacements
                );

                // VRC state behaviours can contain arbitrary user object fields. Preserve
                // VRCFury's generic rewrite only when the batched dependency graph proves
                // that at least one behaviour can reach a replaced clip.
                if (nativeDependencies.Any(clipReplacements.ContainsKey)) {
                    foreach (var behaviour in output.OfType<StateMachineBehaviour>()) {
                        ReflectionUtils.InvokeUnwrapped(
                            rewriteInternals,
                            null,
                            new object[] { behaviour, clipReplacements }
                        );
                    }
                }
                rewriteTicks += Timestamp() - started;
            }

            var settingsStarted = Timestamp();
            foreach (var clip in output.OfType<AnimationClip>()) {
                AnimationUtility.SetAnimationClipSettings(
                    clip,
                    AnimationUtility.GetAnimationClipSettings(clip)
                );
            }
            settingsTicks += Timestamp() - settingsStarted;

            var measuredTicks = includeTicks + pushTicks + dependencyTicks + rewriteTicks + settingsTicks;
            if (detailed && measuredTicks > lastStatsTicks) {
                lastStatsTicks = measuredTicks;
                double Ms(long ticks) => ProfilePatches.ToMilliseconds(ticks);
                LastStats = $"visited={visited.Count},output={output.Count},nativeRoots={nativeDependencyRoots.Count}," +
                            $"includeMs={Ms(includeTicks):F1},pushMs={Ms(pushTicks):F1}," +
                            $"depsMs={Ms(dependencyTicks):F1},rewriteMs={Ms(rewriteTicks):F1}," +
                            $"settingsMs={Ms(settingsTicks):F1},clips={clipCount}," +
                            $"didCreateMs={Ms(didCreateTicks):F1}," +
                            $"originalClipMs={Ms(originalClipTicks):F1}," +
                            $"finalizeClipMs={Ms(finalizeClipTicks):F1}," +
                            $"dedupKeyMs={Ms(deduplicateClipTicks):F1}," +
                            $"deduplicated={deduplicatedClips}," +
                            $"replacements={clipReplacements.Count}";
            }

            return output;
        }

        private static string GetGeneratedClipContentKey(AnimationClip clip) {
            var ext = ReflectionUtils.InvokeUnwrapped(getClipExt, null, new object[] { clip });
            if (ext == null) return null;

            // Modified copies of user clips need their original Euler rotation-order
            // repair in FinalizeAsset. Keep those distinct for now; brand-new clips
            // point originalSourceClip back to themselves and are safe to canonicalize.
            var originalSource = extOriginalSourceClip.GetValue(ext) as AnimationClip;
            if (!ReferenceEquals(originalSource, clip)) return null;
            if (!(extCurves.GetValue(ext) is IDictionary curves)) return null;

            var builder = new StringBuilder();
            builder.Append("clip|")
                .Append(Float(clip.frameRate)).Append('|')
                .Append(clip.legacy).Append('|')
                .Append(clip.wrapMode).Append('|');
            AppendBounds(builder, clip.localBounds);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            foreach (var property in GetClipSettingsProperties(settings.GetType())) {
                builder.Append("setting|").Append(property.Name).Append('|')
                    .Append(Value(property.GetValue(settings, null))).AppendLine();
            }

            foreach (var animationEvent in AnimationUtility.GetAnimationEvents(clip)) {
                builder.Append("event|").Append(Float(animationEvent.time)).Append('|')
                    .Append(animationEvent.functionName).Append('|')
                    .Append(Float(animationEvent.floatParameter)).Append('|')
                    .Append(animationEvent.intParameter).Append('|')
                    .Append(animationEvent.stringParameter).Append('|')
                    .Append(ObjectId(animationEvent.objectReferenceParameter)).Append('|')
                    .Append(animationEvent.messageOptions).AppendLine();
            }

            var entries = new List<(EditorCurveBinding Binding, object Curve)>();
            foreach (DictionaryEntry entry in curves) {
                if (!(entry.Key is EditorCurveBinding binding) || entry.Value == null) return null;
                entries.Add((binding, entry.Value));
            }
            foreach (var entry in entries
                         .OrderBy(value => value.Binding.path, StringComparer.Ordinal)
                         .ThenBy(value => value.Binding.type?.AssemblyQualifiedName, StringComparer.Ordinal)
                         .ThenBy(value => value.Binding.propertyName, StringComparer.Ordinal)
                         .ThenBy(value => value.Binding.isPPtrCurve)
                         .ThenBy(value => value.Binding.isDiscreteCurve)) {
                var binding = entry.Binding;
                var curve = entry.Curve;
                var isFloat = (bool)curveIsFloat.GetValue(curve, null);
                builder.Append(isFloat ? "float|" : "object|")
                    .Append(binding.path).Append('|')
                    .Append(binding.type?.AssemblyQualifiedName).Append('|')
                    .Append(binding.propertyName).Append('|')
                    .Append(binding.isPPtrCurve).Append('|')
                    .Append(binding.isDiscreteCurve).AppendLine();

                if (isFloat) {
                    var animationCurve = curveFloatCurve.GetValue(curve, null) as AnimationCurve;
                    if (animationCurve == null) return null;
                    builder.Append("wrap|").Append(animationCurve.preWrapMode).Append('|')
                        .Append(animationCurve.postWrapMode).AppendLine();
                    foreach (var key in animationCurve.keys) {
                        builder.Append("key|").Append(Float(key.time)).Append('|')
                            .Append(Float(key.value)).Append('|')
                            .Append(Float(key.inTangent)).Append('|')
                            .Append(Float(key.outTangent)).Append('|')
                            .Append(Float(key.inWeight)).Append('|')
                            .Append(Float(key.outWeight)).Append('|')
                            .Append(key.weightedMode).AppendLine();
                    }
                } else {
                    var objectCurve = curveObjectCurve.GetValue(curve, null) as ObjectReferenceKeyframe[];
                    if (objectCurve == null) return null;
                    foreach (var key in objectCurve) {
                        builder.Append("key|").Append(Float(key.time)).Append('|')
                            .Append(ObjectId(key.value)).AppendLine();
                    }
                }
            }

            // The key only dedupes within one Collect call, so a fast non-cryptographic
            // hash is sufficient — no per-clip SHA256 instance and hex-string churn.
            return Hash128.Compute(builder.ToString()).ToString();
        }

        private static PropertyInfo[] GetClipSettingsProperties(Type type) {
            // AnimationUtility.GetAnimationClipSettings always returns the same type.
            if (clipSettingsProperties == null) {
                clipSettingsProperties = type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            return clipSettingsProperties;
        }

        private static string Float(float value) {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Value(object value) {
            if (value == null) return "<null>";
            if (value is Object unityObject) return ObjectId(unityObject);
            if (value is IFormattable formatted) {
                return formatted.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        private static string ObjectId(Object value) {
            if (value == null) return "<null>";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId)) {
                return guid + ":" + localId;
            }
            return "instance:" + value.GetInstanceID();
        }

        private static void AppendBounds(StringBuilder builder, Bounds bounds) {
            builder.Append(Float(bounds.center.x)).Append(',')
                .Append(Float(bounds.center.y)).Append(',')
                .Append(Float(bounds.center.z)).Append('|')
                .Append(Float(bounds.extents.x)).Append(',')
                .Append(Float(bounds.extents.y)).Append(',')
                .Append(Float(bounds.extents.z)).AppendLine();
        }

        private static void PushChildren(
            Object current,
            Stack<Object> stack,
            List<Object> nativeDependencyRoots,
            Dictionary<AnimationClip, List<Object>> clipDependencies
        ) {
            switch (current) {
                case AnimatorController controller:
                    var layers = controller.layers;
                    for (var i = layers.Length - 1; i >= 0; i--) {
                        var layer = layers[i];
                        stack.Push(layer.avatarMask);
                        stack.Push(layer.stateMachine);
                    }
                    break;

                case AnimatorStateMachine stateMachine:
                    var behaviours = stateMachine.behaviours;
                    for (var i = behaviours.Length - 1; i >= 0; i--) stack.Push(behaviours[i]);
                    var entryTransitions = stateMachine.entryTransitions;
                    for (var i = entryTransitions.Length - 1; i >= 0; i--) stack.Push(entryTransitions[i]);
                    var anyStateTransitions = stateMachine.anyStateTransitions;
                    for (var i = anyStateTransitions.Length - 1; i >= 0; i--) {
                        stack.Push(anyStateTransitions[i]);
                    }
                    var childStateMachines = stateMachine.stateMachines;
                    for (var i = childStateMachines.Length - 1; i >= 0; i--) {
                        var child = childStateMachines[i];
                        var transitions = stateMachine.GetStateMachineTransitions(child.stateMachine);
                        for (var transitionIndex = transitions.Length - 1; transitionIndex >= 0; transitionIndex--) {
                            stack.Push(transitions[transitionIndex]);
                        }
                        stack.Push(child.stateMachine);
                    }
                    var childStates = stateMachine.states;
                    for (var i = childStates.Length - 1; i >= 0; i--) stack.Push(childStates[i].state);
                    stack.Push(stateMachine.defaultState);
                    break;

                case AnimatorState state:
                    var stateBehaviours = state.behaviours;
                    for (var i = stateBehaviours.Length - 1; i >= 0; i--) stack.Push(stateBehaviours[i]);
                    var stateTransitions = state.transitions;
                    for (var i = stateTransitions.Length - 1; i >= 0; i--) stack.Push(stateTransitions[i]);
                    stack.Push(state.motion);
                    break;

                case BlendTree tree:
                    var children = tree.children;
                    for (var i = children.Length - 1; i >= 0; i--) stack.Push(children[i].motion);
                    break;

                case AnimatorTransitionBase transition:
                    stack.Push(transition.destinationStateMachine);
                    stack.Push(transition.destinationState);
                    break;

                case StateMachineBehaviour behaviour:
                    nativeDependencyRoots.Add(behaviour);
                    break;

                case AnimationClip clip:
                    PushClipDependencies(clip, stack, clipDependencies);
                    break;
            }
        }

        private static void PushClipDependencies(
            AnimationClip clip,
            Stack<Object> stack,
            Dictionary<AnimationClip, List<Object>> clipDependencies
        ) {
            var dependencies = new List<Object>();
            void Add(Object dependency) {
                if (dependency == null) return;
                dependencies.Add(dependency);
                stack.Push(dependency);
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) {
                foreach (var keyframe in AnimationUtility.GetObjectReferenceCurve(clip, binding)) {
                    Add(keyframe.value);
                }
            }
            foreach (var animationEvent in AnimationUtility.GetAnimationEvents(clip)) {
                Add(animationEvent.objectReferenceParameter);
            }
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            Add(settings.additiveReferencePoseClip);
            clipDependencies[clip] = dependencies;
        }

        private static void RewriteMotions(
            IEnumerable<Object> objects,
            IReadOnlyDictionary<Object, Object> replacements
        ) {
            foreach (var state in objects.OfType<AnimatorState>()) {
                var rewritten = RewriteMotion(state.motion, replacements);
                if (rewritten == state.motion) continue;
                state.motion = rewritten;
                EditorUtility.SetDirty(state);
            }

            foreach (var tree in objects.OfType<BlendTree>()) {
                var children = tree.children;
                var changed = false;
                for (var i = 0; i < children.Length; i++) {
                    var rewritten = RewriteMotion(children[i].motion, replacements);
                    if (rewritten == children[i].motion) continue;
                    children[i].motion = rewritten;
                    changed = true;
                }
                if (!changed) continue;
                tree.children = children;
                EditorUtility.SetDirty(tree);
            }
        }

        private static void RewriteClipReferences(
            IEnumerable<AnimationClip> clips,
            IReadOnlyDictionary<Object, Object> replacements
        ) {
            foreach (var clip in clips) {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var changed = false;
                    for (var i = 0; i < keys.Length; i++) {
                        if (keys[i].value == null
                            || !replacements.TryGetValue(keys[i].value, out var replacement)) continue;
                        keys[i].value = replacement;
                        changed = true;
                    }
                    if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                }

                var events = AnimationUtility.GetAnimationEvents(clip);
                var eventsChanged = false;
                foreach (var animationEvent in events) {
                    if (animationEvent.objectReferenceParameter == null
                        || !replacements.TryGetValue(
                            animationEvent.objectReferenceParameter,
                            out var replacement
                        )) continue;
                    animationEvent.objectReferenceParameter = replacement;
                    eventsChanged = true;
                }
                if (eventsChanged) AnimationUtility.SetAnimationEvents(clip, events);

                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                if (settings.additiveReferencePoseClip != null
                    && replacements.TryGetValue(settings.additiveReferencePoseClip, out var referenceReplacement)) {
                    settings.additiveReferencePoseClip = referenceReplacement as AnimationClip;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                }
            }
        }

        private static Motion RewriteMotion(Motion motion, IReadOnlyDictionary<Object, Object> replacements) {
            if (motion != null && replacements.TryGetValue(motion, out var replacement)) {
                return replacement as Motion;
            }
            return motion;
        }
    }
}
