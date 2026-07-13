using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    internal static class ProfilePatches {
        private sealed class Aggregate {
            internal long Count;
            internal long InclusiveTicks;
            internal long SelfTicks;
            internal long MaxTicks;
        }

        private sealed class Frame {
            internal string Key;
            internal MethodBase Method;
            internal long Started;
            internal long ChildTicks;
        }

        private static readonly Dictionary<string, Aggregate> Actions = new Dictionary<string, Aggregate>();
        private static readonly Dictionary<string, Aggregate> Methods = new Dictionary<string, Aggregate>();

        [ThreadStatic] private static Stack<Frame> actionFrames;
        [ThreadStatic] private static Stack<Frame> methodFrames;
        [ThreadStatic] private static bool detailed;

        private static Harmony harmonyInstance;
        private static bool detailedTargetsInstalled;
        private static bool active;
        private static long runStarted;
        private static bool stockRun;
        private static string runAvatar;

        internal static void Install(Harmony harmony, VrcfuryCompat targets) {
            harmonyInstance = harmony;
            detailedTargetsInstalled = false;

            harmony.Patch(
                targets.RunMain,
                prefix: new HarmonyMethod(typeof(ProfilePatches), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(ProfilePatches), nameof(RunFinalizer))
            );
            harmony.Patch(
                targets.ActionCall,
                prefix: new HarmonyMethod(typeof(ProfilePatches), nameof(ActionPrefix)),
                finalizer: new HarmonyMethod(typeof(ProfilePatches), nameof(ActionFinalizer))
            );

            // The per-method patches put permanent Harmony trampolines on VRCFury's hottest
            // internals, so only install them while detailed profiling is wanted. Turning
            // the toggle on later installs them on the spot; turning it off stops the timing
            // per run and sheds the trampolines on the next domain reload.
            if (Settings.DetailedProfiling) EnsureDetailedTargetsInstalled();
        }

        /**
         * Modules append their own hot internals from Install() so their timings show up
         * under detailed profiling without editing the base table.
         */
        internal static void AddDetailedTargets(string typeName, params string[] methodNames) {
            ExtraDetailedTargets.Add((typeName, methodNames));
            if (detailedTargetsInstalled) InstallDetailedTargets(new[] { (typeName, methodNames) });
        }

        internal static void EnsureDetailedTargetsInstalled() {
            if (detailedTargetsInstalled || harmonyInstance == null) return;
            detailedTargetsInstalled = true;
            InstallDetailedTargets(BaseDetailedTargets.Concat(ExtraDetailedTargets));
        }

        private static void InstallDetailedTargets(IEnumerable<(string TypeName, string[] MethodNames)> targets) {
            foreach (var (typeName, methodNames) in targets) {
                foreach (var methodName in methodNames) {
                    foreach (var method in ReflectionUtils.FindDeclaredMethods(typeName, methodName)) {
                        try {
                            harmonyInstance.Patch(
                                method,
                                prefix: new HarmonyMethod(typeof(ProfilePatches), nameof(MethodPrefix)),
                                finalizer: new HarmonyMethod(typeof(ProfilePatches), nameof(MethodFinalizer))
                            );
                        } catch (Exception e) {
                            Log.Warn($"Could not profile {typeName}.{methodName}: {e.Message}");
                        }
                    }
                }
            }
        }

        private static readonly List<(string TypeName, string[] MethodNames)> ExtraDetailedTargets =
            new List<(string, string[])>();

        private static readonly (string TypeName, string[] MethodNames)[] BaseDetailedTargets = {
            ("VF.Service.ArmatureLinkService", new[] {
                "Apply", "ApplyOne", "RewriteSkins", "GetUsageReasons", "GetRootName", "GetLinks"
            }),
            ("VF.Service.ObjectMoveService", new[] { "Move", "ApplyDeferred" }),
            ("VF.Service.FindAnimatedTransformsService", new[] { "Find" }),
            ("VF.Service.AllClipsService", new[] { "RewriteAllClips" }),
            ("VF.Utils.PhysboneUtils", new[] { "RemoveFromPhysbones" }),
            ("VF.Utils.VFGameObject", new[] { "GetConstraints", "Destroy" }),
            ("VF.Service.LayerToTreeService", new[] { "Apply", "OptimizeLayer", "GetBindingsAnimatedInLayer" }),
            ("VF.Service.SaveAssetsService", new[] { "Run" }),
            ("VF.Utils.SaveAssetsSession", new[] {
                "SaveUnsavedComponentAssets", "GetUnsavedChildren", "SaveAssetAndChildren", "RecordWorkLog",
                "FlushWorkLogManifest", "WriteWorkLogManifest"
            }),
            ("VF.Utils.VRCFuryAssetDatabase", new[] {
                "SaveAsset", "AttachAsset", "CreateFolder", "GetUniquePath", "WithoutAssetEditing"
            }),
            ("VF.Utils.MutableManager", new[] {
                "ForEachChild", "ForEachChildObjectReference", "RewriteInternals"
            }),
            ("VF.Inspector.VRCFuryHapticSocketEditor", new[] { "Bake" }),
            ("VF.Builder.Haptics.SpsUpgrader", new[] { "Apply" }),
            ("VF.Service.HapticContactsService", new[] { "AddReceiver" }),
            ("VF.Service.HapticAnimContactsService", new[] { "CreateAnims" }),
            ("VF.Builder.Haptics.PlugSizeDetector", new[] { "GetAutoWorldSize" }),
            ("VF.Builder.MeshBaker", new[] { "BakeMesh" }),
            ("VF.Inspector.VRCFuryHapticPlugEditor", new[] { "GetRenderers" }),
            ("VF.Builder.Haptics.TpsConfigurer", new[] { "HasDpsOrTpsMaterial" }),
            ("VF.Builder.Haptics.PlugRendererFinder", new[] { "GetAutoRenderer" }),
            ("VF.Builder.Haptics.PlugMaskGenerator", new[] { "GetMask" })
        };

        private static void RunPrefix(object __0) {
            var module = ProfilingModule.Instance;
            if (module == null || !module.Enabled) {
                active = false;
                return;
            }
            Actions.Clear();
            Methods.Clear();
            actionFrames = new Stack<Frame>();
            detailed = Settings.DetailedProfiling;
            methodFrames = detailed ? new Stack<Frame>() : null;
            stockRun = BakeHistory.BenchmarkPending;
            try {
                // RunMain's single argument is the avatar (VFGameObject; ToString = scene path).
                runAvatar = __0?.ToString() ?? "";
            } catch {
                runAvatar = "";
            }
            runStarted = Stopwatch.GetTimestamp();
            active = true;
        }

        private static Exception RunFinalizer(Exception __exception) {
            if (!active) return __exception;

            var elapsed = Stopwatch.GetTimestamp() - runStarted;
            active = false;
            // One-shot: the benchmark flag survives exactly one measured bake, pass or fail.
            if (stockRun) BakeHistory.BenchmarkPending = false;
            if (__exception == null) {
                try {
                    BakeHistory.RecordBake(
                        ToMilliseconds(elapsed), runAvatar, MeaningfulPhases(), stockRun);
                } catch {
                    // History is cosmetic; recording must never break a bake.
                }
            }
            FuryPlusPlusProfilerApi.SetLastReport(BuildReport(elapsed, __exception));
            UnityEngine.Debug.Log(FuryPlusPlusProfilerApi.LastReport);
            return __exception;
        }

        /**
         * Every phase that ran, down to 1 ms, capped so the persisted payload stays small.
         * The floor must stay near zero: any higher and a phase FuryPlusPlus speeds up
         * below the floor drops out of the record entirely, which the breakdown then
         * mislabels as "removed". Deciding what is big enough to *show* is the settings
         * window's job, not the recorder's.
         */
        private static List<(string Name, double Ms)> MeaningfulPhases() {
            const double floor = 1.0;
            return Actions
                .OrderByDescending(pair => pair.Value.InclusiveTicks)
                .Select(pair => (pair.Key, ToMilliseconds(pair.Value.InclusiveTicks)))
                .Where(pair => pair.Item2 >= floor)
                .Take(128)
                .ToList();
        }

        private static void ActionPrefix(object __instance) {
            if (!active) return;

            string key;
            try {
                var targets = Bootstrap.Compat;
                var service = targets.ActionGetService.Invoke(__instance, null);
                var methodName = targets.ActionGetName.Invoke(__instance, null) as string ?? "?";
                key = (service?.GetType().Name ?? "?") + "." + methodName;
            } catch {
                key = "UnknownAction";
            }

            actionFrames.Push(new Frame { Key = key, Started = Stopwatch.GetTimestamp() });
        }

        private static Exception ActionFinalizer(Exception __exception) {
            if (!active || actionFrames == null || actionFrames.Count == 0) return __exception;

            var frame = actionFrames.Pop();
            var elapsed = Stopwatch.GetTimestamp() - frame.Started;
            Add(Actions, frame.Key, elapsed, elapsed);
            return __exception;
        }

        private static readonly Dictionary<MethodBase, string> MethodKeys =
            new Dictionary<MethodBase, string>();
        // The detailed targets include VRCFury's hottest internals, so composed
        // "action > method" keys are cached per pair instead of concatenated per call.
        private static readonly Dictionary<(string, MethodBase), string> ComposedKeys =
            new Dictionary<(string, MethodBase), string>();

        private static string BuildKey(MethodBase method) {
            if (!MethodKeys.TryGetValue(method, out var key)) {
                key = method.DeclaringType?.Name + "." + method.Name;
                MethodKeys[method] = key;
            }
            if (actionFrames == null || actionFrames.Count == 0) return key;

            var actionKey = actionFrames.Peek().Key;
            if (!ComposedKeys.TryGetValue((actionKey, method), out var composed)) {
                composed = actionKey + " > " + key;
                ComposedKeys[(actionKey, method)] = composed;
            }
            return composed;
        }

        private static void MethodPrefix(MethodBase __originalMethod) {
            if (!active || !detailed) return;

            methodFrames.Push(new Frame {
                Key = BuildKey(__originalMethod),
                Method = __originalMethod,
                Started = Stopwatch.GetTimestamp()
            });
        }

        private static Exception MethodFinalizer(MethodBase __originalMethod, Exception __exception) {
            if (!active || !detailed || methodFrames == null || methodFrames.Count == 0) {
                return __exception;
            }

            var frame = methodFrames.Pop();
            // Reference-compare the original method instead of recomputing the composed
            // key; the action context cannot change within one profiled call.
            if (!ReferenceEquals(frame.Method, __originalMethod)) {
                methodFrames.Clear();
                return __exception;
            }
            var elapsed = Stopwatch.GetTimestamp() - frame.Started;
            var self = Math.Max(0, elapsed - frame.ChildTicks);
            Add(Methods, frame.Key, elapsed, self);

            if (methodFrames.Count > 0) {
                methodFrames.Peek().ChildTicks += elapsed;
            }
            return __exception;
        }

        private static void Add(Dictionary<string, Aggregate> output, string key, long inclusive, long self) {
            if (!output.TryGetValue(key, out var aggregate)) {
                aggregate = new Aggregate();
                output[key] = aggregate;
            }
            aggregate.Count++;
            aggregate.InclusiveTicks += inclusive;
            aggregate.SelfTicks += self;
            aggregate.MaxTicks = Math.Max(aggregate.MaxTicks, inclusive);
        }

        private static string BuildReport(long elapsedTicks, Exception exception) {
            var builder = new StringBuilder();
            builder.AppendLine(
                $"[FuryPlusPlus] VRCFury profile: {ToMilliseconds(elapsedTicks):F3} ms total" +
                (stockRun ? " (stock benchmark — FuryPlusPlus modules disabled)" : "") +
                (exception == null ? "" : $" (failed: {exception.GetType().Name})")
            );
            if (exception != null) {
                builder.AppendLine("Failure: " + FormatException(exception));
            }
            builder.AppendLine("Top actions (exact call duration):");
            AppendAggregates(builder, Actions, 40);

            if (detailed) {
                builder.AppendLine("Detailed internals (inclusive / self / calls / max):");
                AppendAggregates(builder, Methods, 80);
            }

            foreach (var module in ModuleRegistry.All) {
                string stats = null;
                try {
                    stats = module.ReportStats();
                } catch {
                    // A stats formatter must never break the report.
                }
                if (stats != null) builder.AppendLine($"  {module.Id}: {stats}");
            }
            builder.AppendLine("Modules: " + ModuleRegistry.DescribeStates());
            return builder.ToString();
        }

        private static string FormatException(Exception exception) {
            var parts = new List<string>();
            for (var current = exception; current != null && parts.Count < 6; current = current.InnerException) {
                parts.Add(current.GetType().Name + ": " + current.Message.Replace('\r', ' ').Replace('\n', ' '));
            }
            return string.Join(" -> ", parts);
        }

        private static void AppendAggregates(
            StringBuilder builder,
            Dictionary<string, Aggregate> values,
            int limit
        ) {
            foreach (var pair in values.OrderByDescending(pair => pair.Value.InclusiveTicks).Take(limit)) {
                var value = pair.Value;
                builder.AppendLine(
                    $"{ToMilliseconds(value.InclusiveTicks),12:F3} / " +
                    $"{ToMilliseconds(value.SelfTicks),12:F3} / " +
                    $"{value.Count,7} / {ToMilliseconds(value.MaxTicks),12:F3} ms  {pair.Key}"
                );
            }
        }

        internal static double ToMilliseconds(long ticks) {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        /** Currently-executing VRCFury action and its elapsed time, if a profiled run is live. */
        internal static (string Key, double ElapsedMs)? CurrentAction() {
            var frames = actionFrames;
            if (!active || frames == null || frames.Count == 0) return null;
            var frame = frames.Peek();
            return (frame.Key, ToMilliseconds(Stopwatch.GetTimestamp() - frame.Started));
        }
    }

    /** The one intentional public surface — external scripts read the last bake report here. */
    public static class FuryPlusPlusProfilerApi {
        public static string LastReport { get; private set; }

        internal static void SetLastReport(string report) {
            LastReport = report ?? "";
            SessionState.SetString("FuryPlusPlus.LastProfile", LastReport);
        }
    }
}
