using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * Replaces only the compiler-generated path-mapping lambda inside
     * ObjectMoveService.ApplyDeferred. Controller/clip traversal remains VRCFury's own code.
     */
    internal sealed class OrderedPathRewriteModule : Module {
        internal static OrderedPathRewriteModule Instance { get; private set; }

        internal static readonly ModuleOption SkipEmptyDeferredOption = new ModuleOption(
            "skipEmptyDeferred",
            "Skip empty deferred rewrites",
            true,
            "Skip the full identity rewrite of every managed clip and mask when no deferred moves were recorded."
        );

        internal OrderedPathRewriteModule() {
            Instance = this;
        }

        internal override string Id => "orderedPathRewrite";
        internal override string DisplayName => "Ordered path rewrite";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Trie-based chronological path rewriting for deferred Armature Link moves.";

        internal override IReadOnlyList<ModuleOption> Options => new[] { SkipEmptyDeferredOption };

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ArmatureCompat.EnsureResolved();
            OrderedPathRewritePatch.Install(harmony, compat);
        }
    }

    internal static class OrderedPathRewritePatch {
        [ThreadStatic] private static object activeService;
        [ThreadStatic] private static OrderedPathResolver activeResolver;

        private static FieldInfo deferredMovesField;

        internal static void Install(Harmony harmony, VrcfuryCompat targets) {
            // PORT-NOTE: ApplyDeferred/ApplyDeferredPathLambda/DeferredMoves lived on QuickFury's
            // VrcfuryCompatibility; VrcfuryCompat has no such members, so resolve them here with
            // the same predicates as QuickFury's VrcfuryCompatibility.TryCreate.
            var objectMoveServiceType = ReflectionUtils.FindType("VF.Service.ObjectMoveService");
            var applyDeferred = ReflectionUtils.FindUniqueMethod(
                objectMoveServiceType,
                "ApplyDeferred",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 0
            );
            var lambdaCandidates = objectMoveServiceType?
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => method.Name.Contains("ApplyDeferred"))
                .Where(method => method.ReturnType == typeof(string))
                .Where(method => {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                })
                .Take(2).ToList();
            var applyDeferredPathLambda = lambdaCandidates?.Count == 1 ? lambdaCandidates[0] : null;
            deferredMovesField = objectMoveServiceType?
                .GetField("deferred", BindingFlags.Instance | BindingFlags.NonPublic);

            if (applyDeferred == null || applyDeferredPathLambda == null || deferredMovesField == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                applyDeferred,
                prefix: new HarmonyMethod(typeof(OrderedPathRewritePatch), nameof(BeginDeferredRewrite)),
                finalizer: new HarmonyMethod(typeof(OrderedPathRewritePatch), nameof(EndDeferredRewrite))
            );
            harmony.Patch(
                applyDeferredPathLambda,
                prefix: new HarmonyMethod(typeof(OrderedPathRewritePatch), nameof(RewritePath))
            );
        }

        private static bool BeginDeferredRewrite(object __instance) {
            activeService = null;
            activeResolver = null;

            // PORT-NOTE: QuickFury's two independent flags map to module Enabled + a ModuleOption.
            // Literal substitution means the skip-empty option still acts while the module itself
            // is disabled, matching QuickFury's independent-flag behavior exactly.
            if (OrderedPathRewriteModule.Instance?.Enabled != true
                && !Settings.IsOptionEnabled(OrderedPathRewriteModule.Instance, OrderedPathRewriteModule.SkipEmptyDeferredOption)) {
                return true;
            }

            var moves = ReadMoves(__instance);
            if (moves.Count == 0
                && Settings.IsOptionEnabled(OrderedPathRewriteModule.Instance, OrderedPathRewriteModule.SkipEmptyDeferredOption)) {
                // The original implementation performs a complete identity rewrite of every managed
                // clip and mask, then clears an already-empty list.
                return false;
            }

            if (OrderedPathRewriteModule.Instance?.Enabled == true && moves.Count >= 2) {
                activeService = __instance;
                activeResolver = new OrderedPathResolver(moves);
            }

            return true;
        }

        private static Exception EndDeferredRewrite(object __instance, Exception __exception) {
            if (ReferenceEquals(activeService, __instance)) {
                activeService = null;
                activeResolver = null;
            }
            return __exception;
        }

        private static bool RewritePath(object __instance, string __0, ref string __result) {
            if (!ReferenceEquals(activeService, __instance) || activeResolver == null) {
                return true;
            }

            __result = activeResolver.Rewrite(__0);
            return false;
        }

        private static List<(string from, string to)> ReadMoves(object service) {
            var output = new List<(string from, string to)>();
            var deferredMoves = deferredMovesField;
            if (service == null || deferredMoves == null) return output;

            var raw = deferredMoves.GetValue(service) as IEnumerable;
            if (raw == null) return output;

            foreach (var item in raw) {
                if (item is ValueTuple<string, string> move) {
                    output.Add((move.Item1, move.Item2));
                }
            }
            return output;
        }
    }
}
