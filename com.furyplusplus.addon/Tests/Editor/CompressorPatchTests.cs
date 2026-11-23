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
        public void Sub8PatchTargetsPassedActionController() {
            RequireInstalled();

            var parameters = CompressorCompat.LayerBuildLayer.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(CompressorCompat.DecisionType, parameters[0].ParameterType);
            Assert.AreEqual(CompressorCompat.AvatarControllerType, parameters[1].ParameterType);
            Assert.AreEqual("controller", parameters[1].Name);
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

        // ---- sub-8 driver entry lookup (VRCFury 1.1470.0 and the later encoding layout) ----

        private static VRC.SDKBase.VRC_AvatarParameterDriver.Parameter Copy(string source, string name) {
            return new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter {
                type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy, source = source, name = name
            };
        }

        private static VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver Driver(
            params VRC.SDKBase.VRC_AvatarParameterDriver.Parameter[] entries
        ) {
            var driver = UnityEngine.ScriptableObject.CreateInstance<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
            driver.parameters = entries.ToList();
            return driver;
        }

        [TestCase("VF7_SyncDataNum1", 1, true)]
        [TestCase("VF_Ab3dE9xZ_SyncDataNum1", 1, true)]
        [TestCase("SyncDataNum1", 1, true)]
        [TestCase("VF_Ab3dE9xZ_SyncDataNum10", 1, false)]
        [TestCase("VF_Ab3dE9xZ_SyncDataNum1", 0, false)]
        [TestCase("VF_Ab3dE9xZ_MySyncDataNum1", 1, false)]
        public void Sub8RecognizesNumberSlotNames(string name, int slot, bool expected) {
            Assert.AreEqual(expected, Sub8Surgery.IsNumberSlot(name, slot));
        }

        [Test]
        public void Sub8FindsLegacyLayoutEntriesByName() {
            // VRCFury 1.1470.0: latch entries first, then one copy per slot; one receive copy.
            var send = Driver(
                Copy("Late", "VF7_Late"),
                Copy("Other", "VF7_SyncDataNum0"),
                Copy("Rep", "VF7_SyncDataNum1"));
            var receive = Driver(
                Copy("VF7_SyncDataNum0", "Other"),
                Copy("VF7_SyncDataNum1", "Rep"));

            var (sendEntry, prelude, chain) = Sub8Surgery.LocateForTest(send, receive, "Rep", 1);

            Assert.AreSame(send.parameters[2], sendEntry);
            Assert.IsEmpty(prelude);
            CollectionAssert.AreEqual(new[] { receive.parameters[1] }, chain);
        }

        [Test]
        public void Sub8FindsScratchEncodedFloatSendAndRemovesOnlyItsOwnPrelude() {
            // Later releases round batch-0 floats through one shared scratch value.
            const string scratch = "VF_buildHsh_Compressor Value";
            var send = Driver(
                Copy("Late", scratch),
                Copy(scratch, "VF_buildHsh_Late/Latched"),
                Copy("Other", scratch),
                Copy(scratch, "VF_avatarHs_SyncDataNum0"),
                Copy("Rep", scratch),
                Copy(scratch, "VF_avatarHs_SyncDataNum1"),
                Copy("Unrelated", "VF_avatarHs_SyncDataNum10"));
            var receive = Driver(
                Copy("VF_avatarHs_SyncDataNum0", "Other"),
                Copy("VF_avatarHs_SyncDataNum1", "Rep"),
                Copy("VF_avatarHs_SyncDataNum10", "Unrelated"));

            var (sendEntry, prelude, chain) = Sub8Surgery.LocateForTest(send, receive, "Rep", 1);

            Assert.AreSame(send.parameters[5], sendEntry);
            CollectionAssert.AreEqual(new[] { send.parameters[4] }, prelude);
            CollectionAssert.AreEqual(new[] { receive.parameters[1] }, chain);
        }

        [Test]
        public void Sub8FollowsAMobileReencodeChainToTheOriginal() {
            // Mobile, where the local declaration differs from the desktop wire format.
            const string scratch = "VF_buildHsh_Compressor Value";
            const string code = "VF_buildHsh_Compressor Code";
            var send = Driver(Copy("Rep", scratch), Copy(scratch, "VF_avatarHs_SyncDataNum0"));
            var receive = Driver(
                Copy("VF_avatarHs_SyncDataNum0", scratch),
                Copy(scratch, code),
                Copy(code, "Rep"),
                Copy("VF_avatarHs_SyncDataBool0", "Flag"));

            var (_, _, chain) = Sub8Surgery.LocateForTest(send, receive, "Rep", 0);

            CollectionAssert.AreEqual(receive.parameters.Take(3), chain);
        }

        [Test]
        public void Sub8RejectsABrokenReceiveChain() {
            var send = Driver(Copy("Rep", "VF_avatarHs_SyncDataNum0"));
            var receive = Driver(
                Copy("VF_avatarHs_SyncDataNum0", "VF_buildHsh_Compressor Value"),
                Copy("Something else", "Rep"));

            Assert.Throws<Exception>(() => Sub8Surgery.LocateForTest(send, receive, "Rep", 0));
        }

#if VRCSDK_HAS_DYNAMIC_PARAMETER_BIT_WIDTH
        [Test]
        public void Sub8PairsOnlyStandardFloats() {
            CompressorScope.Sub8Active = true;
            CompressorScope.Sub8Globs = Globs.Parse("pack/*");
            var standardA = new VRCExpressionParameters.Parameter { name = "pack/a", valueType = VRCExpressionParameters.ValueType.Float };
            var narrow = new VRCExpressionParameters.Parameter { name = "pack/narrow", valueType = VRCExpressionParameters.ValueType.Float, bitWidth = 5 };
            var unsigned = new VRCExpressionParameters.Parameter { name = "pack/unsigned", valueType = VRCExpressionParameters.ValueType.Float, flippedRepresentation = true };
            var standardB = new VRCExpressionParameters.Parameter { name = "pack/b", valueType = VRCExpressionParameters.ValueType.Float };

            var pairs = CompressorScope.ComputeSub8Pairs(new List<VRCExpressionParameters.Parameter> {
                standardA, narrow, unsigned, standardB
            });

            Assert.AreEqual(1, pairs.Count);
            Assert.AreSame(standardA, pairs[0].Rep);
            Assert.AreSame(standardB, pairs[0].Partner);
        }
#endif
    }
}
