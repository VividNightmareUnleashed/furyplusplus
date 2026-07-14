using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * Randomized invariants for the patched OptimizationDecision.GetBatches (lane packing
     * + sub-8-bit pair removal) plus the sub-8 quantizer math. Exercises the REAL VRCFury
     * type through the installed Harmony patch; ignored when the compressor modules
     * aren't installed (VRCFury absent or incompatible).
     */
    public class CompressorPatchTests {
        private bool savedRunActive;
        private bool savedLanePacking;
        private bool savedSub8;
        private List<System.Text.RegularExpressions.Regex> savedGlobs;

        [SetUp]
        public void SetUp() {
            savedRunActive = CompressorScope.RunActive;
            savedLanePacking = CompressorScope.LanePackingActive;
            savedSub8 = CompressorScope.Sub8Active;
            savedGlobs = CompressorScope.Sub8Globs;
        }

        [TearDown]
        public void TearDown() {
            CompressorScope.RunActive = savedRunActive;
            CompressorScope.LanePackingActive = savedLanePacking;
            CompressorScope.Sub8Active = savedSub8;
            CompressorScope.Sub8Globs = savedGlobs;
        }

        private static void RequireInstalled() {
            if (CompressorLanePackingModule.Instance == null
                || !ModuleRegistry.IsActive(CompressorLanePackingModule.Instance)) {
                Assert.Ignore("Compressor modules not installed (VRCFury absent or incompatible).");
            }
            CompressorCompat.EnsureResolved();
        }

        private static VRCExpressionParameters.Parameter Param(
            string name, VRCExpressionParameters.ValueType type) {
            return new VRCExpressionParameters.Parameter { name = name, valueType = type };
        }

        private static object MakeDecision(
            IList<VRCExpressionParameters.Parameter> compress, int numberSlots, int boolSlots) {
            var decision = Activator.CreateInstance(CompressorCompat.DecisionType);
            CompressorCompat.DecisionCompress.SetValue(decision, compress);
            CompressorCompat.DecisionNumberSlots.SetValue(decision, numberSlots);
            CompressorCompat.DecisionBoolSlots.SetValue(decision, boolSlots);
            return decision;
        }

        private static (List<List<VRCExpressionParameters.Parameter>> Numbers,
            List<List<VRCExpressionParameters.Parameter>> Bools) Batches(object decision) {
            var result = CompressorCompat.DecisionGetBatches.Invoke(decision, null);
            return (
                (List<List<VRCExpressionParameters.Parameter>>)CompressorCompat.BatchesItem1.GetValue(result),
                (List<List<VRCExpressionParameters.Parameter>>)CompressorCompat.BatchesItem2.GetValue(result)
            );
        }

        [Test]
        public void LanePackingInvariants_Randomized() {
            RequireInstalled();
            var rng = new Random(20260713);

            for (var iteration = 0; iteration < 300; iteration++) {
                var numberCount = rng.Next(0, 40);
                var boolCount = rng.Next(0, 90);
                var numberSlots = numberCount > 0 ? rng.Next(1, numberCount + 1) : 0;
                var boolSlots = boolCount > 0 ? rng.Next(1, boolCount + 1) : 0;

                var compress = new List<VRCExpressionParameters.Parameter>();
                var numbers = new List<VRCExpressionParameters.Parameter>();
                var bools = new List<VRCExpressionParameters.Parameter>();
                for (var i = 0; i < numberCount + boolCount; i++) {
                    var isBool = bools.Count < boolCount
                                 && (numbers.Count >= numberCount || rng.Next(2) == 0);
                    var parameter = Param($"p{i}", isBool
                        ? VRCExpressionParameters.ValueType.Bool
                        : (rng.Next(2) == 0
                            ? VRCExpressionParameters.ValueType.Float
                            : VRCExpressionParameters.ValueType.Int));
                    (isBool ? bools : numbers).Add(parameter);
                    compress.Add(parameter);
                }

                var decision = MakeDecision(compress, numberSlots, boolSlots);

                CompressorScope.RunActive = false; // patch inert → stock geometry
                var stock = Batches(decision);
                var stockBatchCount = Math.Max(stock.Numbers.Count, stock.Bools.Count);

                CompressorScope.RunActive = true;
                CompressorScope.LanePackingActive = true;
                CompressorScope.Sub8Active = false;
                var packed = Batches(decision);
                CompressorScope.RunActive = false;

                var context = $"iter={iteration} n={numberCount} b={boolCount} " +
                              $"ns={numberSlots} bs={boolSlots}";

                // Every param appears exactly once across all batches.
                var all = packed.Numbers.SelectMany(batch => batch)
                    .Concat(packed.Bools.SelectMany(batch => batch)).ToList();
                Assert.AreEqual(compress.Count, all.Count, context);
                CollectionAssert.AreEquivalent(compress, all, context);

                // Slot bounds hold.
                Assert.IsTrue(packed.Numbers.All(batch => batch.Count <= numberSlots), context);
                Assert.IsTrue(packed.Bools.All(batch => batch.Count <= boolSlots), context);

                // Never more batches than stock.
                var packedBatchCount = Math.Max(packed.Numbers.Count, packed.Bools.Count);
                Assert.LessOrEqual(packedBatchCount, stockBatchCount, context);

                // Number order preserved; bool order preserved (bool lanes first, then
                // the overflow that moved into the int lanes).
                CollectionAssert.AreEqual(
                    numbers,
                    packed.Numbers.SelectMany(batch => batch)
                        .Where(p => p.valueType != VRCExpressionParameters.ValueType.Bool).ToList(),
                    context);
                CollectionAssert.AreEqual(
                    bools,
                    packed.Bools.SelectMany(batch => batch)
                        .Concat(packed.Numbers.SelectMany(batch => batch)
                            .Where(p => p.valueType == VRCExpressionParameters.ValueType.Bool))
                        .ToList(),
                    context);
            }
        }

        [Test]
        public void Sub8PairRemoval_RemovesPartnersKeepsEverythingElse() {
            RequireInstalled();

            var floats = Enumerable.Range(0, 5)
                .Select(i => Param($"pack/f{i}", VRCExpressionParameters.ValueType.Float)).ToList();
            var other = Param("other", VRCExpressionParameters.ValueType.Float);
            var boolParam = Param("b0", VRCExpressionParameters.ValueType.Bool);
            var compress = new List<VRCExpressionParameters.Parameter>();
            compress.AddRange(floats);
            compress.Add(other);
            compress.Add(boolParam);

            var decision = MakeDecision(compress, 3, 1);

            CompressorScope.RunActive = true;
            CompressorScope.LanePackingActive = false;
            CompressorScope.Sub8Active = true;
            CompressorScope.Sub8Globs = Globs.Parse("pack/*");
            var pairs = CompressorScope.ComputeSub8Pairs(compress);
            var batches = Batches(decision);
            CompressorScope.RunActive = false;

            // 5 matching floats → 2 pairs, odd one left on the stock path.
            Assert.AreEqual(2, pairs.Count);
            Assert.AreEqual(floats[0], pairs[0].Rep);
            Assert.AreEqual(floats[1], pairs[0].Partner);
            Assert.AreEqual(floats[2], pairs[1].Rep);
            Assert.AreEqual(floats[3], pairs[1].Partner);

            var numberParams = batches.Numbers.SelectMany(batch => batch).ToList();
            CollectionAssert.DoesNotContain(numberParams, floats[1]);
            CollectionAssert.DoesNotContain(numberParams, floats[3]);
            CollectionAssert.Contains(numberParams, floats[0]);
            CollectionAssert.Contains(numberParams, floats[2]);
            CollectionAssert.Contains(numberParams, floats[4]);
            CollectionAssert.Contains(numberParams, other);
            CollectionAssert.Contains(batches.Bools.SelectMany(batch => batch).ToList(), boolParam);
        }

        [Test]
        public void QuantizerRoundtripsAllSixteenSteps() {
            for (var index = 0; index <= 15; index++) {
                var value = Sub8Surgery.DecodeValue(index);
                Assert.GreaterOrEqual(value, -1f);
                Assert.LessOrEqual(value, 1f);
                Assert.AreEqual(index, Sub8Surgery.QuantizeIndex(value),
                    $"decode({index})={value} must re-quantize to {index}");
            }
        }

        [Test]
        public void QuantizerClampsAndCoversRange() {
            Assert.AreEqual(0, Sub8Surgery.QuantizeIndex(-1f));
            Assert.AreEqual(0, Sub8Surgery.QuantizeIndex(-5f));
            Assert.AreEqual(15, Sub8Surgery.QuantizeIndex(1f));
            Assert.AreEqual(15, Sub8Surgery.QuantizeIndex(5f));
            Assert.AreEqual(11, Sub8Surgery.QuantizeIndex(0.5f));
            Assert.AreEqual(8, Sub8Surgery.QuantizeIndex(0.0667f));
        }
    }
}
