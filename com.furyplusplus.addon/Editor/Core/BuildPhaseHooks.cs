using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * Shared mid-build seam: modules register callbacks that fire before/after a named
     * VRCFury FeatureOrder phase. One prefix on FeatureBuilderAction.Call dispatches on the
     * action's priority (actions execute in ascending priority order); a RunMain prefix/
     * finalizer scopes the run and flushes end-of-build "after" hooks.
     *
     * FeatureOrder members are resolved BY NAME (the enum has no explicit values) —
     * registration throws MissingMemberException for unknown names, so a consuming module
     * fails closed at its own Install().
     */
    internal static class BuildPhaseHooks {
        private sealed class Hook {
            internal string ModuleId;
            internal string PhaseName;
            internal int Threshold;
            internal bool After;
            internal Action<object> Callback;
            internal bool Fired;
            internal bool Broken;
        }

        private static readonly List<Hook> Hooks = new List<Hook>();
        private static bool installed;
        private static bool runActive;
        private static Type featureOrderType;
        private static MethodInfo actionGetPriorty;
        private static MethodInfo actionGetService;

        internal static bool Installed => installed;

        internal static void Install(Harmony harmony, VrcfuryCompat compat) {
            installed = false;
            Hooks.Clear();

            featureOrderType = ReflectionUtils.Demand(
                compat.AvatarsEditorAssembly.GetType("VF.Feature.Base.FeatureOrder", false),
                "VF.Feature.Base.FeatureOrder"
            );
            var actionType = ReflectionUtils.Demand(
                compat.AvatarsEditorAssembly.GetType("VF.Feature.Base.FeatureBuilderAction", false),
                "VF.Feature.Base.FeatureBuilderAction"
            );
            // VRCFury's typo: the getter really is named GetPriorty.
            actionGetPriorty = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(
                    actionType,
                    "GetPriorty",
                    method => method.GetParameters().Length == 0 && method.ReturnType.IsEnum
                ),
                "FeatureBuilderAction.GetPriorty()"
            );
            actionGetService = compat.ActionGetService;

            harmony.Patch(
                compat.RunMain,
                prefix: new HarmonyMethod(typeof(BuildPhaseHooks), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(BuildPhaseHooks), nameof(RunFinalizer))
            );
            harmony.Patch(
                compat.ActionCall,
                prefix: new HarmonyMethod(typeof(BuildPhaseHooks), nameof(CallPrefix))
            );
            installed = true;
        }

        /** Fires before the first action whose priority is >= the named phase. */
        internal static void RegisterBefore(string phaseName, string moduleId, Action<object> callback) {
            Register(phaseName, moduleId, callback, after: false);
        }

        /**
         * Fires before the first action whose priority is > the named phase — i.e. after
         * every action AT that phase completed — or at successful build end if none follows.
         */
        internal static void RegisterAfter(string phaseName, string moduleId, Action<object> callback) {
            Register(phaseName, moduleId, callback, after: true);
        }

        private static void Register(string phaseName, string moduleId, Action<object> callback, bool after) {
            if (!installed) {
                throw new InvalidOperationException("BuildPhaseHooks is not installed");
            }
            int threshold;
            try {
                threshold = Convert.ToInt32(Enum.Parse(featureOrderType, phaseName));
            } catch (Exception) {
                throw new MissingMemberException("VRCFury member not found: FeatureOrder." + phaseName);
            }
            Hooks.Add(new Hook {
                ModuleId = moduleId,
                PhaseName = phaseName,
                Threshold = threshold,
                After = after,
                Callback = callback
            });
        }

        private static void RunPrefix() {
            foreach (var hook in Hooks) {
                hook.Fired = false;
                hook.Broken = false;
            }
            runActive = true;
        }

        private static void CallPrefix(object __instance) {
            if (!runActive || Hooks.Count == 0) return;

            int priority;
            object service;
            try {
                priority = Convert.ToInt32(actionGetPriorty.Invoke(__instance, null));
                service = actionGetService.Invoke(__instance, null);
            } catch {
                // Never break the build over dispatch bookkeeping.
                return;
            }

            foreach (var hook in Hooks) {
                if (hook.Fired || hook.Broken) continue;
                var atBoundary = hook.After ? priority > hook.Threshold : priority >= hook.Threshold;
                if (!atBoundary) continue;
                hook.Fired = true;
                Fire(hook, service);
            }
        }

        private static Exception RunFinalizer(Exception __exception) {
            if (!runActive) return __exception;
            runActive = false;

            if (__exception == null) {
                // Phases at the very end of the build have no later action; flush their
                // "after" hooks now. Skipped when the build failed — half-built state.
                foreach (var hook in Hooks) {
                    if (hook.Fired || hook.Broken || !hook.After) continue;
                    hook.Fired = true;
                    Fire(hook, null);
                }
            }
            return __exception;
        }

        private static void Fire(Hook hook, object service) {
            try {
                hook.Callback(service);
            } catch (Exception e) {
                // Disable every callback of the offending module for the rest of this build.
                foreach (var other in Hooks) {
                    if (other.ModuleId == hook.ModuleId) other.Broken = true;
                }
                Log.Warn(
                    $"{hook.ModuleId}: phase hook {(hook.After ? "after" : "before")} {hook.PhaseName} " +
                    $"failed and is disabled for this build: {e.Message}"
                );
            }
        }
    }
}
