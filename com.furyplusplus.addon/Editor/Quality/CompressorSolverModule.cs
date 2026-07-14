using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Replaces the parameter compressor's greedy solver with an exhaustive search.
     * Stock walks seven menu-type option sets with an accept-if-half-the-time heuristic
     * and expands slot counts greedily; this module evaluates every (option set ×
     * numberSlots × boolSlots) combination that fits under the bit limit and picks the
     * one with the fewest batches per sync (ties: fewer compressed params, less
     * aggressive set, lower cost). Runs on desktop builds only — mobile builds replay
     * the desktop decision from VRCFury's own alignment file, so the solved result
     * carries over automatically. Any error falls back to the stock solver.
     *
     * Composes with lane packing / sub-8-bit lanes: the search evaluates batch counts
     * through the patched GetBatches, so it optimizes the packed geometry directly.
     *
     * Known delta: stock's advisory "you could unsync these" warnings are not
     * reproduced when this solver wins (the strip-unused-params module automates the
     * main case instead).
     */
    internal sealed class CompressorSolverModule : Module<CompressorSolverModule> {

        internal override string Id => "compressorSolver";
        internal override string DisplayName => "Compressor: exhaustive solver";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Parameter compressor (sync bits)";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Searches every compressor option-set and slot-count combination instead of " +
            "VRCFury's greedy walk, minimizing batches per sync (= sync delay) under the bit " +
            "limit. Desktop-only; Quest builds inherit the result through VRCFury's own " +
            "alignment file. Falls back to the stock solver on any error.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorScope.EnsureInstalled(harmony);
            CompressorSolverPatch.Resolve();
            harmony.Patch(
                CompressorCompat.SolverPublicSolve,
                prefix: new HarmonyMethod(typeof(CompressorSolverPatch), nameof(CompressorSolverPatch.Prefix))
            );
        }

        internal override string ReportStats() {
            return CompressorScope.SolverStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return CompressorScope.ReportedSolverBatches > 0
                ? ($"{CompressorScope.ReportedSolverBatches} batches per sync last bake",
                    CompressorScope.SolverStats)
                : ((string, string)?)null;
        }
    }

    internal static class CompressorSolverPatch {
        private static PropertyInfo controllerLayers;   // VFController.layers
        private static PropertyInfo layerAllBehaviours; // VFLayer.allBehaviours

        internal static void Resolve() {
            CompressorCompat.DemandCore();
            ReflectionUtils.Demand(CompressorCompat.SolverPublicSolve,
                "ParameterCompressorSolverService.GetParamsToOptimize()");
            ReflectionUtils.Demand(CompressorCompat.SolverPrivateSolve,
                "ParameterCompressorSolverService.GetParamsToOptimize(5 args)");
            ReflectionUtils.Demand(CompressorCompat.SolverParamsService,
                "ParameterCompressorSolverService.paramsService");
            ReflectionUtils.Demand(CompressorCompat.SolverControllers,
                "ParameterCompressorSolverService.controllers");
            ReflectionUtils.Demand(CompressorCompat.ParamsGetReadOnly, "ParamsService.GetReadOnlyParams()");
            ReflectionUtils.Demand(CompressorCompat.ParamsClone,
                "ObjectExtensions.Clone<VRCExpressionParameters>(...)");
            ReflectionUtils.Demand(CompressorCompat.GetMaxCost,
                "VRCExpressionParametersExtensions.GetMaxCost()");
            ReflectionUtils.Demand(CompressorCompat.ControllersGetAllReadOnly,
                "ControllersService.GetAllReadOnlyControllers()");
            ReflectionUtils.Demand(CompressorCompat.SolverOutputType,
                "VF.Service.Compressor.ParameterCompressorSolverOutput");
            ReflectionUtils.Demand(CompressorCompat.OutputDecision, "ParameterCompressorSolverOutput.decision");
            ReflectionUtils.Demand(CompressorCompat.OutputOptions, "ParameterCompressorSolverOutput.options");
            ReflectionUtils.Demand(CompressorCompat.SelectionOptionsType, "ParamSelectionOptions");
            ReflectionUtils.Demand(CompressorCompat.OptionsAllowedMenuTypes,
                "ParamSelectionOptions.allowedMenuTypes");
            ReflectionUtils.Demand(CompressorCompat.CompressorMenuItemGet, "CompressorMenuItem.Get()");

            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"),
                "VF.Utils.Controller.VFController");
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            controllerLayers = ReflectionUtils.Demand(
                vfControllerType.GetProperty("layers", any), "VFController.layers");
            var layerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFLayer"), "VF.Utils.Controller.VFLayer");
            layerAllBehaviours = ReflectionUtils.Demand(
                layerType.GetProperty("allBehaviours", any), "VFLayer.allBehaviours");
        }

        internal static bool Prefix(object __instance, ref object __result) {
            if (!CompressorScope.RunActive || !CompressorScope.SolverActive) return true;
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target == BuildTarget.Android || target == BuildTarget.iOS) return true;
            try {
                var solved = Solve(__instance);
                if (solved == null) return true;
                __result = solved;
                return false;
            } catch (Exception e) {
                Log.Warn("Compressor solver fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        // The seven option sets stock tries, in stock's least-aggressive-first order.
        private static readonly VRCExpressionsMenu.Control.ControlType[][] OptionSets = {
            new[] { VRCExpressionsMenu.Control.ControlType.RadialPuppet },
            new[] { VRCExpressionsMenu.Control.ControlType.Toggle },
            new[] { VRCExpressionsMenu.Control.ControlType.RadialPuppet, VRCExpressionsMenu.Control.ControlType.Toggle },
            new[] { VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet, VRCExpressionsMenu.Control.ControlType.FourAxisPuppet },
            new[] { VRCExpressionsMenu.Control.ControlType.RadialPuppet, VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet, VRCExpressionsMenu.Control.ControlType.FourAxisPuppet },
            new[] { VRCExpressionsMenu.Control.ControlType.Toggle, VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet, VRCExpressionsMenu.Control.ControlType.FourAxisPuppet },
            new[] { VRCExpressionsMenu.Control.ControlType.RadialPuppet, VRCExpressionsMenu.Control.ControlType.Toggle, VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet, VRCExpressionsMenu.Control.ControlType.FourAxisPuppet },
        };

        /** Returns the solver output, or null to fall back to the stock solver. */
        private static object Solve(object solver) {
            var paramsService = CompressorCompat.SolverParamsService.GetValue(solver);
            var readOnlyParams = CompressorCompat.ParamsGetReadOnly.Invoke(paramsService, null);
            var paramz = (VRCExpressionParameters)CompressorCompat.ParamsClone
                .Invoke(null, new[] { readOnlyParams, null, "", (object)true });
            var originalCost = paramz.CalcTotalCost();
            var maxCost = (int)CompressorCompat.GetMaxCost.Invoke(null, null);
            if (originalCost <= maxCost) {
                return Activator.CreateInstance(CompressorCompat.SolverOutputType);
            }

            var addDriven = CollectAddDrivenParams(solver);

            object bestDecision = null;
            var bestBatches = int.MaxValue;
            var bestCost = int.MaxValue;
            var bestCompressCount = int.MaxValue;
            var bestSetIndex = int.MaxValue;
            var bestSlots = int.MaxValue;
            var bestNumberSlots = 0;
            var bestBoolSlots = 0;
            VRCExpressionsMenu.Control.ControlType[] bestSet = null;
            var evaluated = 0;

            for (var setIndex = 0; setIndex < OptionSets.Length; setIndex++) {
                var allowed = new HashSet<VRCExpressionsMenu.Control.ControlType>(OptionSets[setIndex]);
                var decision = ReflectionUtils.InvokeUnwrapped(CompressorCompat.SolverPrivateSolve, solver,
                    new object[] { paramz, allowed, addDriven, originalCost, false });
                var compress = (IList<VRCExpressionParameters.Parameter>)
                    CompressorCompat.DecisionCompress.GetValue(decision);
                if (compress.Count == 0) continue;

                var boolCount = compress.Count(p => p.valueType == VRCExpressionParameters.ValueType.Bool);
                var numberCount = compress.Count - boolCount;
                var maxNumberSlots = Math.Min(numberCount, 32);
                var maxBoolSlots = Math.Min(boolCount, 255);

                for (var numberSlots = numberCount > 0 ? 1 : 0; numberSlots <= (numberCount > 0 ? maxNumberSlots : 0); numberSlots++) {
                    for (var boolSlots = boolCount > 0 ? 1 : 0; boolSlots <= (boolCount > 0 ? maxBoolSlots : 0); boolSlots++) {
                        CompressorCompat.DecisionNumberSlots.SetValue(decision, numberSlots);
                        CompressorCompat.DecisionBoolSlots.SetValue(decision, boolSlots);
                        var cost = (int)CompressorCompat.DecisionGetFinalCost
                            .Invoke(decision, new object[] { originalCost });
                        if (cost > maxCost) continue;
                        var batches = (int)CompressorCompat.DecisionGetBatchCount.Invoke(decision, null);
                        evaluated++;

                        var better =
                            batches < bestBatches
                            || (batches == bestBatches && compress.Count < bestCompressCount)
                            || (batches == bestBatches && compress.Count == bestCompressCount
                                && setIndex < bestSetIndex)
                            || (batches == bestBatches && compress.Count == bestCompressCount
                                && setIndex == bestSetIndex && cost < bestCost)
                            || (batches == bestBatches && compress.Count == bestCompressCount
                                && setIndex == bestSetIndex && cost == bestCost
                                && numberSlots + boolSlots < bestSlots);
                        if (!better) continue;
                        bestBatches = batches;
                        bestCost = cost;
                        bestCompressCount = compress.Count;
                        bestSetIndex = setIndex;
                        bestSlots = numberSlots + boolSlots;
                        bestNumberSlots = numberSlots;
                        bestBoolSlots = boolSlots;
                        bestSet = OptionSets[setIndex];
                        bestDecision = decision;
                    }
                }
            }

            if (bestDecision == null) return null;

            // The grid left the last-tried slot values on the decision; restore the winners.
            CompressorCompat.DecisionNumberSlots.SetValue(bestDecision, bestNumberSlots);
            CompressorCompat.DecisionBoolSlots.SetValue(bestDecision, bestBoolSlots);

            var setting = CompressorCompat.CompressorMenuItemGet.Invoke(null, null)?.ToString();
            if (setting == "Fail") return null;
            if (setting == "Ask") {
                var msg = $"Your avatar is out of space for parameters! Your avatar uses {originalCost}/{maxCost} bits." +
                          " VRCFury can compress your parameters to fit, at the expense of slightly slower toggle" +
                          " syncing in game. Is this okay?\n\n(Solved by FuryPlusPlus: " +
                          $"{bestBatches} batches per sync, {bestCost}/{maxCost} bits.)";
                var ok = EditorUtility.DisplayDialog("Out of parameter space", msg,
                    "Ok (Accept Compression)", "Fail the Build");
                if (!ok) return null; // stock solver runs and applies its own failure flow
            }

            var output = Activator.CreateInstance(CompressorCompat.SolverOutputType);
            CompressorCompat.OutputDecision.SetValue(output, bestDecision);
            var options = Activator.CreateInstance(CompressorCompat.SelectionOptionsType);
            CompressorCompat.OptionsAllowedMenuTypes.SetValue(options,
                new List<VRCExpressionsMenu.Control.ControlType>(bestSet));
            CompressorCompat.OutputOptions.SetValue(output, options);

            CompressorScope.LastSolverBatches = bestBatches;
            CompressorScope.LastSolverImprovement =
                $"{bestBatches} batches per sync at {bestCost}/{maxCost} bits " +
                $"({bestCompressCount} params compressed, {evaluated} combinations searched)";
            return output;
        }

        private static ISet<string> CollectAddDrivenParams(object solver) {
            var addDriven = new HashSet<string>();
            var controllers = CompressorCompat.SolverControllers.GetValue(solver);
            foreach (var controller in (IEnumerable)CompressorCompat.ControllersGetAllReadOnly
                         .Invoke(controllers, null)) {
                foreach (var layer in (IEnumerable)controllerLayers.GetValue(controller)) {
                    foreach (var behaviour in (IEnumerable)layerAllBehaviours.GetValue(layer)) {
                        if (!(behaviour is VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver driver)) continue;
                        foreach (var parameter in driver.parameters) {
                            if (parameter.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Add) {
                                addDriven.Add(parameter.name);
                            }
                        }
                    }
                }
            }
            return addDriven;
        }
    }
}
