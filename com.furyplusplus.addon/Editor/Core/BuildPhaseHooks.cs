using System;
using System.Collections.Generic;
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
        internal sealed class Hook {
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
        private static UnityEngine.GameObject currentAvatarRoot;

        internal static bool Installed => installed;

        /** Root of the avatar currently being built (null outside a build). */
        internal static UnityEngine.GameObject CurrentAvatarRoot => runActive ? currentAvatarRoot : null;

        /**
         * Resolves a live per-build VRCFury service (same injector instance the build uses).
         * Returns null when unavailable — callers must treat that as "skip this run".
         */
        internal static object GetService(string typeFullName) {
            try {
                if (!runActive || currentAvatarRoot == null) return null;
                var descriptor = currentAvatarRoot
                    .GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (descriptor == null) return null;
                return BuildPhaseCompat.GetService(descriptor, typeFullName);
            } catch {
                return null;
            }
        }

        internal static void Install(Harmony harmony, VrcfuryCompat compat) {
            installed = false;
            Hooks.Clear();

            BuildPhaseCompat.Resolve(compat);
            VfGameObjectCompat.DemandCore();

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
            var threshold = BuildPhaseCompat.GetThreshold(phaseName);
            Hooks.Add(new Hook {
                ModuleId = moduleId,
                PhaseName = phaseName,
                Threshold = threshold,
                After = after,
                Callback = callback
            });
        }

        private static void RunPrefix(object __0) {
            foreach (var hook in Hooks) {
                hook.Fired = false;
                hook.Broken = false;
            }
            try {
                currentAvatarRoot = VfGameObjectCompat.Unwrap(__0);
            } catch {
                currentAvatarRoot = null;
            }
            runActive = true;
        }

        private static void CallPrefix(object __instance) {
            if (!runActive || Hooks.Count == 0) return;

            // The ~7 hooks each fire at most once per build; skip the reflection invokes
            // for every action after the last one has fired.
            var anyPending = false;
            foreach (var hook in Hooks) {
                if (!hook.Fired && !hook.Broken) { anyPending = true; break; }
            }
            if (!anyPending) return;

            int priority;
            try {
                priority = BuildPhaseCompat.GetPriority(__instance);
            } catch {
                // Never break the build over dispatch bookkeeping.
                return;
            }

            // The service is only needed when a hook actually crosses its boundary here.
            object service = null;
            var serviceFetched = false;
            foreach (var hook in Hooks) {
                if (hook.Fired || hook.Broken) continue;
                var atBoundary = hook.After ? priority > hook.Threshold : priority >= hook.Threshold;
                if (!atBoundary) continue;
                if (!serviceFetched) {
                    try {
                        service = BuildPhaseCompat.GetActionService(__instance);
                    } catch {
                        return;
                    }
                    serviceFetched = true;
                }
                hook.Fired = true;
                Fire(hook, service);
            }
        }

        private static Exception RunFinalizer(Exception __exception) {
            if (!runActive) return __exception;

            if (__exception == null) {
                // Phases at the very end of the build have no later action; flush their
                // "after" hooks now — BEFORE clearing run state, so GetService and
                // CurrentAvatarRoot still work inside them. Skipped when the build failed.
                foreach (var hook in Hooks) {
                    if (hook.Fired || hook.Broken || !hook.After) continue;
                    hook.Fired = true;
                    Fire(hook, null);
                }
            }
            runActive = false;
            currentAvatarRoot = null;
            return __exception;
        }

        internal static void Fire(Hook hook, object service) {
            if (!ModuleRegistry.IsOn(ModuleRegistry.Find(hook.ModuleId))) return;
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
