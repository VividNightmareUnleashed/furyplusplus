using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Shared runtime for the compressor modules. One prefix/finalizer pair on
     * ParameterCompressorService.Apply latches every module's kill switch and option list
     * for the run (EditorPrefs at phase boundaries only); one postfix on
     * OptimizationDecision.GetBatches applies the geometry changes:
     *
     *  1. sub-8-bit pair removal: the second float of each configured pair rides its
     *     representative's int lane, so it leaves the number stream entirely;
     *  2. trailing-bool lane packing: when there are more bool batches than number
     *     batches, the trailing bools move into the idle int-lane slots of the trailing
     *     batches (drivers copy bools into int slots losslessly), shrinking the batch
     *     count — which shrinks sync time and, transitively, the index bit count.
     *
     * Both transforms are pure functions of (compress order, numberSlots, boolSlots,
     * configured lists), which is exactly the tuple VRCFury's own mobile alignment file
     * replays — so a desktop and mobile build with the same FuryPlusPlus inputs derive the
     * same wire layout. The FppSidecar hard-fails mobile builds whose inputs differ.
     *
     * Every batch-geometry consumer (GetFinalCost→GetIndexBitCount, GetBatchCount,
     * BuildLayer, sync-time estimates) flows through GetBatches, so cost accounting stays
     * self-consistent automatically.
     */
    internal static class CompressorScope {
        private static bool installed;

        internal static bool RunActive;
        internal static bool LanePackingActive;
        internal static bool Sub8Active;
        internal static bool SolverActive;
        internal static bool EligibilityActive;
        internal static List<Regex> Sub8Globs = new List<Regex>();
        internal static List<Regex> EligibilityGlobs = new List<Regex>();

        // Stats for the last Apply run (logged once from the finalizer).
        internal static int LastMovedBools;
        internal static int LastStockBatches;
        internal static int LastPackedBatches;
        internal static int LastSub8Pairs;
        internal static int LastEligibilityAdded;
        internal static string LastSolverImprovement;

        internal static string LanePackingStats;
        internal static string Sub8Stats;
        internal static string SolverStats;
        internal static string EligibilityStats;

        /** EditorPrefs (string): semicolon-separated wildcard patterns of float params to 4-bit pack. */
        internal const string Sub8ListKey = Settings.KeyPrefix + "module.compressorSub8.precisionList";

        /** EditorPrefs (string): semicolon-separated wildcard patterns of extra params to compress. */
        internal const string EligibilityListKey = Settings.KeyPrefix + "module.compressorEligibility.extraList";

        internal static void EnsureInstalled(Harmony harmony) {
            if (installed) return;
            CompressorCompat.DemandCore();
            harmony.Patch(
                CompressorCompat.CompressorApply,
                prefix: new HarmonyMethod(typeof(CompressorScope), nameof(ApplyPrefix)),
                finalizer: new HarmonyMethod(typeof(CompressorScope), nameof(ApplyFinalizer))
            );
            harmony.Patch(
                CompressorCompat.DecisionGetBatches,
                postfix: new HarmonyMethod(typeof(CompressorScope), nameof(BatchesPostfix))
            );
            if (CompressorCompat.LayerBuildLayer != null) {
                // The solver's grid stamps packing stats thousands of times; reset just
                // before the real layer build so the logged numbers describe the final
                // geometry, not a mid-search candidate.
                harmony.Patch(
                    CompressorCompat.LayerBuildLayer,
                    prefix: new HarmonyMethod(typeof(CompressorScope), nameof(BuildLayerPrefix))
                );
            }
            installed = true;
        }

        private static void BuildLayerPrefix() {
            LastMovedBools = 0;
            LastStockBatches = 0;
            LastPackedBatches = 0;
            LastSub8Pairs = 0;
        }

        private static bool IsOn(Module module) {
            return module != null && ModuleRegistry.IsActive(module) && module.Enabled;
        }

        private static void ApplyPrefix() {
            LanePackingActive = IsOn(CompressorLanePackingModule.Instance);
            Sub8Active = IsOn(CompressorSub8Module.Instance);
            Sub8Globs = Sub8Active
                ? ParseGlobs(EditorPrefs.GetString(Sub8ListKey, ""))
                : new List<Regex>();
            if (Sub8Globs.Count == 0) Sub8Active = false;
            SolverActive = IsOn(CompressorSolverModule.Instance);
            EligibilityActive = IsOn(CompressorEligibilityModule.Instance);
            EligibilityGlobs = EligibilityActive
                ? ParseGlobs(EditorPrefs.GetString(EligibilityListKey, ""))
                : new List<Regex>();
            if (EligibilityGlobs.Count == 0) EligibilityActive = false;
            LastMovedBools = 0;
            LastStockBatches = 0;
            LastPackedBatches = 0;
            LastSub8Pairs = 0;
            LastEligibilityAdded = 0;
            LastSolverImprovement = null;
            RunActive = true;
        }

        private static Exception ApplyFinalizer(Exception __exception) {
            RunActive = false;
            if (LastMovedBools > 0) {
                Log.Info($"Compressor lane packing: moved {LastMovedBools} trailing bool(s) into idle " +
                         $"int lanes ({LastStockBatches} → {LastPackedBatches} batches per sync).");
                LanePackingStats = $"movedBools={LastMovedBools} batches={LastStockBatches}->{LastPackedBatches}";
            }
            if (LastSub8Pairs > 0) {
                Log.Info($"Compressor sub-8-bit lanes: {LastSub8Pairs} float pair(s) share an int lane " +
                         "at 4-bit precision each.");
                Sub8Stats = $"packedPairs={LastSub8Pairs}";
            }
            if (LastEligibilityAdded > 0) {
                Log.Info($"Compressor eligibility: {LastEligibilityAdded} listed parameter(s) added to " +
                         "the compressible set.");
                EligibilityStats = $"extraParams={LastEligibilityAdded}";
            }
            if (LastSolverImprovement != null) {
                Log.Info("Compressor solver: " + LastSolverImprovement);
                SolverStats = LastSolverImprovement;
            }
            return __exception;
        }

        private static void BatchesPostfix(object __instance, object __result) {
            if (!RunActive || (!LanePackingActive && !Sub8Active)) return;
            try {
                if ((bool)CompressorCompat.DecisionUseBadPriority.GetValue(__instance)) return;

                var numberBatches =
                    (List<List<VRCExpressionParameters.Parameter>>)CompressorCompat.BatchesItem1.GetValue(__result);
                var boolBatches =
                    (List<List<VRCExpressionParameters.Parameter>>)CompressorCompat.BatchesItem2.GetValue(__result);
                var numberSlots = (int)CompressorCompat.DecisionNumberSlots.GetValue(__instance);
                var boolSlots = (int)CompressorCompat.DecisionBoolSlots.GetValue(__instance);

                var numbers = numberBatches.SelectMany(batch => batch).ToList();
                var bools = boolBatches.SelectMany(batch => batch).ToList();
                var changed = false;

                if (Sub8Active) {
                    var compress = (IList<VRCExpressionParameters.Parameter>)
                        CompressorCompat.DecisionCompress.GetValue(__instance);
                    var pairs = ComputeSub8Pairs(compress);
                    if (pairs.Count > 0) {
                        var partners = new HashSet<VRCExpressionParameters.Parameter>(
                            pairs.Select(pair => pair.Partner));
                        var removed = numbers.RemoveAll(partners.Contains);
                        if (removed > 0) changed = true;
                        LastSub8Pairs = pairs.Count;
                    }
                }

                if (LanePackingActive && numberSlots > 0 && bools.Count > 0) {
                    var numberBatchCount = numbers.Count == 0
                        ? 0
                        : (numbers.Count + numberSlots - 1) / numberSlots;
                    var boolBatchCount = boolSlots == 0
                        ? int.MaxValue
                        : (bools.Count + boolSlots - 1) / boolSlots;
                    if (boolBatchCount > numberBatchCount) {
                        var stockBatchCount = Math.Max(numberBatchCount, boolBatchCount);
                        // Smallest batch count whose bool lanes + idle int slots hold every bool.
                        var target = Math.Max(1, numberBatchCount);
                        while ((long)target * boolSlots + ((long)target * numberSlots - numbers.Count)
                               < bools.Count) {
                            target++;
                        }
                        if (target < stockBatchCount) {
                            var keepInBoolLanes = Math.Min(bools.Count, target * boolSlots);
                            var overflow = bools.Skip(keepInBoolLanes).ToList();
                            bools = bools.Take(keepInBoolLanes).ToList();
                            numbers.AddRange(overflow);
                            LastMovedBools = overflow.Count;
                            LastStockBatches = stockBatchCount;
                            LastPackedBatches = target;
                            changed = true;
                        }
                    }
                }

                if (!changed) return;
                RebuildChunks(numberBatches, numbers, numberSlots);
                RebuildChunks(boolBatches, bools, boolSlots);
            } catch (Exception e) {
                // Geometry rewrites must never break a build: leave stock batches in place.
                LanePackingActive = false;
                Sub8Active = false;
                Log.Warn("Compressor batch packing fell back to VRCFury: " + e.Message);
            }
        }

        private static void RebuildChunks(
            List<List<VRCExpressionParameters.Parameter>> target,
            List<VRCExpressionParameters.Parameter> items,
            int chunkSize
        ) {
            target.Clear();
            if (chunkSize <= 0) return;
            for (var start = 0; start < items.Count; start += chunkSize) {
                target.Add(items.Skip(start).Take(chunkSize).ToList());
            }
        }

        /**
         * The configured float params that share int lanes, paired consecutively in
         * compress-list order (an odd leftover stays on the stock full-precision path).
         * Pure function of the decision's compress list + the configured globs, so desktop
         * and mobile (which replays the desktop compress list) derive identical pairs.
         */
        private static IList<VRCExpressionParameters.Parameter> lastPairsKey;
        private static List<(VRCExpressionParameters.Parameter, VRCExpressionParameters.Parameter)> lastPairs;

        internal static List<(VRCExpressionParameters.Parameter Rep, VRCExpressionParameters.Parameter Partner)>
            ComputeSub8Pairs(IList<VRCExpressionParameters.Parameter> compress) {
            // The solver's slot-count grid calls GetBatches thousands of times on the same
            // decision; cache by compress-list reference so the glob matching runs once.
            if (ReferenceEquals(compress, lastPairsKey) && lastPairs != null) return lastPairs;
            var result = new List<(VRCExpressionParameters.Parameter, VRCExpressionParameters.Parameter)>();
            lastPairsKey = compress;
            lastPairs = result;
            if (!Sub8Active || Sub8Globs.Count == 0) return result;
            var matching = compress
                .Where(parameter => parameter != null
                                    && parameter.valueType == VRCExpressionParameters.ValueType.Float
                                    && !string.IsNullOrEmpty(parameter.name)
                                    && Sub8Globs.Any(glob => glob.IsMatch(parameter.name)))
                .ToList();
            for (var i = 0; i + 1 < matching.Count; i += 2) {
                result.Add((matching[i], matching[i + 1]));
            }
            return result;
        }

        internal static List<Regex> ParseGlobs(string raw) {
            var globs = new List<Regex>();
            foreach (var entry in raw.Split(';')) {
                var trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;
                var pattern = "^" + Regex.Escape(trimmed).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                globs.Add(new Regex(pattern, RegexOptions.CultureInvariant));
            }
            return globs;
        }
    }
}
