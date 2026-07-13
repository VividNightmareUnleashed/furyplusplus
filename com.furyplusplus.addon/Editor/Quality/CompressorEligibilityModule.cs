using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Stock compressor eligibility only covers params it can see driven by menu
     * controls. This module appends user-listed synced params (wildcards, default empty
     * list = inert) to the compressible set — for params the user knows change rarely
     * (e.g. outfit state driven by FullController logic) that stock refuses to compress.
     *
     * Momentary params are excluded automatically: params used by menu Buttons and params
     * written with driver Add (both change faster than the batch cadence can replicate).
     * Appended params keep params-asset order, and the decision is re-optimized after the
     * append. Desktop-only in effect: mobile replays the desktop decision from VRCFury's
     * alignment file.
     */
    internal sealed class CompressorEligibilityModule : Module {
        internal static CompressorEligibilityModule Instance { get; private set; }

        internal CompressorEligibilityModule() {
            Instance = this;
        }

        internal override string Id => "compressorEligibility";
        internal override string DisplayName => "Compressor: extra eligible params (opt-in list)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Adds listed synced parameters to the compressor's eligible set (params stock " +
            "won't compress because no menu control drives them). Inert until the list is " +
            "filled: " + CompressorScope.EligibilityListKey + " (semicolon-separated wildcards). " +
            "Menu-button params and Add-driven params are excluded automatically.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorScope.EnsureInstalled(harmony);
            ReflectionUtils.Demand(CompressorCompat.SolverPrivateSolve,
                "ParameterCompressorSolverService.GetParamsToOptimize(5 args)");
            ReflectionUtils.Demand(CompressorCompat.SolverGetParamsUsedInMenu,
                "ParameterCompressorSolverService.GetParamsUsedInMenu(types)");
            ReflectionUtils.Demand(CompressorCompat.DecisionOptimize, "OptimizationDecision.Optimize(int)");
            harmony.Patch(
                CompressorCompat.SolverPrivateSolve,
                postfix: new HarmonyMethod(typeof(CompressorEligibilityModule), nameof(SolvePostfix))
            );
        }

        private static void SolvePostfix(
            object __instance,
            object __result,
            object __0, // VRCExpressionParameters paramz
            object __2, // ISet<string> addDriven
            int __3,    // originalCost
            bool __4    // useBadPriorityMethod
        ) {
            if (!CompressorScope.RunActive || !CompressorScope.EligibilityActive || __4) return;
            try {
                var compress = (IList<VRCExpressionParameters.Parameter>)
                    CompressorCompat.DecisionCompress.GetValue(__result);
                var present = new HashSet<string>(compress
                    .Where(parameter => parameter != null)
                    .Select(parameter => parameter.name));
                var addDriven = (ISet<string>)__2;

                var buttonTypes = new HashSet<VRCExpressionsMenu.Control.ControlType> {
                    VRCExpressionsMenu.Control.ControlType.Button,
                    VRCExpressionsMenu.Control.ControlType.SubMenu
                };
                var buttonParams = (ISet<string>)ReflectionUtils.InvokeUnwrapped(
                    CompressorCompat.SolverGetParamsUsedInMenu, __instance, new object[] { buttonTypes });

                var additions = ((VRCExpressionParameters)__0).parameters
                    .Where(parameter => parameter != null
                                        && parameter.networkSynced
                                        && !string.IsNullOrEmpty(parameter.name)
                                        && !present.Contains(parameter.name)
                                        && !buttonParams.Contains(parameter.name)
                                        && !addDriven.Contains(parameter.name)
                                        && CompressorScope.EligibilityGlobs
                                            .Any(glob => glob.IsMatch(parameter.name)))
                    .ToList();
                if (additions.Count == 0) return;

                CompressorCompat.DecisionCompress.SetValue(__result, compress.Concat(additions).ToList());
                CompressorCompat.DecisionOptimize.Invoke(__result, new object[] { __3 });
                CompressorScope.LastEligibilityAdded = additions.Count;
            } catch (Exception e) {
                Log.Warn("Compressor eligibility widening fell back to VRCFury: " + e.Message);
            }
        }

        internal override string ReportStats() {
            return CompressorScope.EligibilityStats;
        }
    }
}
