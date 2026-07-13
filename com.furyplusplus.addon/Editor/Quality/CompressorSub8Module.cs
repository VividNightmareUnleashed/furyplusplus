using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace FuryPlusPlus {
    /**
     * Packs two user-listed float parameters into one 8-bit int sync lane at 4-bit
     * precision each (16 steps across the -1..1 range — noticeable on radials, which is
     * why this is strictly opt-in per param via the precision list; default empty).
     *
     * Send side: an always-on direct-blendtree layer quantizes both floats through
     * plateau step-trees that SUM into one AAP (hi*16 + lo); the batch's send state
     * driver-copies that AAP into the int slot instead of the stock full-precision copy.
     *
     * Receive side: a 256-child 1D decode tree (evaluated in a layer BEFORE the
     * compressor layer) turns the slot value back into two decoded AAPs; a one-frame
     * interstitial state after the batch's receive state driver-copies them onto the
     * original params — the extra frame guarantees the decode tree has evaluated the
     * newly-arrived slot value before the copy fires.
     *
     * Pairing is a pure function of the decision's compress order + the configured list,
     * both of which VRCFury's own mobile alignment replays — so desktop and Quest uploads
     * derive the same wire layout when FuryPlusPlus runs with the same list on both.
     *
     * Deliberate exception to the fail-open rule: once the batch geometry reserves a
     * shared lane, a failed surgery would silently upload an avatar whose listed params
     * never sync — so surgery failure FAILS THE BUILD instead (validate-then-mutate makes
     * this effectively unreachable on the pinned VRCFury version).
     *
     * Known deltas (documented): packed params send live values instead of latched ones,
     * and received values apply at their batch instead of at cycle end.
     */
    internal sealed class CompressorSub8Module : Module {
        internal static CompressorSub8Module Instance { get; private set; }

        internal CompressorSub8Module() {
            Instance = this;
        }

        internal override string Id => "compressorSub8";
        internal override string DisplayName => "Compressor: 4-bit float pairs (opt-in list)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Packs pairs of listed float parameters into one int sync lane at 4-bit precision " +
            "each (16 steps — visible on radials, so list only params where that's acceptable). " +
            "Inert until the precision list is filled: " + CompressorScope.Sub8ListKey + " " +
            "(semicolon-separated wildcards). Quest uploads of the same avatar need FuryPlusPlus " +
            "with the same list.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorScope.EnsureInstalled(harmony);
            Sub8Surgery.Resolve();
            harmony.Patch(
                CompressorCompat.LayerBuildLayer,
                postfix: new HarmonyMethod(typeof(CompressorSub8Module), nameof(BuildLayerPostfix))
            );
        }

        private static void BuildLayerPostfix(object __instance, object __0) {
            if (!CompressorScope.RunActive || !CompressorScope.Sub8Active) return;
            Sub8Surgery.Run(__instance, __0);
        }

        internal override string ReportStats() {
            return CompressorScope.Sub8Stats;
        }
    }

    internal static class Sub8Surgery {
        private static System.Reflection.MethodInfo controllerManagerOne; // ControllerManager.One() → VFAFloat
        private static System.Reflection.MethodInfo vfaApName;            // BlendtreeMath.VFAap.Name()
        private static System.Reflection.MethodInfo factoryCreate;       // VrcfObjectFactory.Create(Type, Object)

        internal static void Resolve() {
            CompressorCompat.DemandCore();
            ReflectionUtils.Demand(CompressorCompat.LayerBuildLayer,
                "ParameterCompressorLayerService.BuildLayer(decision)");
            ReflectionUtils.Demand(CompressorCompat.LayerServiceControllers,
                "ParameterCompressorLayerService.controllers");
            ReflectionUtils.Demand(CompressorCompat.GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(CompressorCompat.DecisionGetIndexBitCount,
                "OptimizationDecision.GetIndexBitCount()");
            ReflectionUtils.Demand(CompressorCompat.MakeAap, "ControllerManager.MakeAap(string, float, bool)");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.GetRaw, "VFController.GetRaw()");
            ReflectionUtils.Demand(ToggleTreeCompat.NewLayer, "ControllerManager.NewLayer(string, int)");
            ReflectionUtils.Demand(ToggleTreeCompat.NewState, "VFLayer.NewState(string)");
            ReflectionUtils.Demand(ToggleTreeCompat.StateWithAnimation, "VFState.WithAnimation(Motion)");
            ReflectionUtils.Demand(VfLayerCompat.RootStateMachineField, "VFLayer.rootStateMachine");

            var controllerManagerType = ReflectionUtils.FindType("VF.Utils.ControllerManager");
            controllerManagerOne = ReflectionUtils.Demand(
                controllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                    controllerManagerType, "One", method => method.GetParameters().Length == 0),
                "ControllerManager.One()");
            var vfaApType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath+VFAap");
            vfaApName = ReflectionUtils.Demand(
                vfaApType == null ? null : ReflectionUtils.FindUniqueMethod(
                    vfaApType, "Name", method => method.GetParameters().Length == 0),
                "BlendtreeMath.VFAap.Name()");

            // Everything we add must be factory-created: the SaveAssets pass that runs right
            // after the compressor only attaches VrcfObjectFactory-created objects to the
            // controller asset (and stops walking through anything else) — unattached
            // sub-objects lose their cross-references when the asset reserializes.
            var factoryType = ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory");
            factoryCreate = ReflectionUtils.Demand(
                factoryType == null ? null : ReflectionUtils.FindUniqueMethod(
                    factoryType, "Create", method => !method.IsGenericMethodDefinition
                                                     && method.GetParameters().Length == 2
                                                     && method.GetParameters()[0].ParameterType == typeof(Type)),
                "VrcfObjectFactory.Create(Type, Object)");
            ReflectionUtils.Demand(CompressorCompat.ClipSetAap, "AnimationClipExtensions.SetAap(clip, name, curve)");
            ReflectionUtils.Demand(CompressorCompat.FloatToCurve, "FloatOrObjectCurve.op_Implicit(float)");
        }

        private static T Create<T>() where T : UnityEngine.Object {
            return (T)factoryCreate.Invoke(null, new object[] { typeof(T), null });
        }

        private sealed class PairPlan {
            internal VRCExpressionParameters.Parameter Rep;
            internal VRCExpressionParameters.Parameter Partner;
            internal int BatchNum;
            internal VRCAvatarParameterDriver SendDriver;
            internal VRC_AvatarParameterDriver.Parameter SendEntry;
            internal VRCAvatarParameterDriver ReceiveDriver;
            internal VRC_AvatarParameterDriver.Parameter ReceiveEntry;
            internal VRCAvatarParameterDriver UnlatchDriver;   // null when rep is in the last batch
            internal VRC_AvatarParameterDriver.Parameter UnlatchEntry;
            internal string SlotParamName;
            internal string PackedAap;
            internal string HiOutAap;
            internal string LoOutAap;
        }

        internal static void Run(object layerService, object decision) {
            var compress = (IList<VRCExpressionParameters.Parameter>)
                CompressorCompat.DecisionCompress.GetValue(decision);
            var pairs = CompressorScope.ComputeSub8Pairs(compress);
            if (pairs.Count == 0) return;
            if ((bool)CompressorCompat.DecisionUseBadPriority.GetValue(decision)) return;

            // ---- gather everything first; throw (failing the build) before any mutation ----
            var controllers = CompressorCompat.LayerServiceControllers.GetValue(layerService);
            var fx = CompressorCompat.GetFx.Invoke(controllers, null);
            var fxRaw = (AnimatorController)ToggleTreeCompat.GetRaw.Invoke(fx, null);

            var batchesObj = CompressorCompat.DecisionGetBatches.Invoke(decision, null);
            var numberBatches = (List<List<VRCExpressionParameters.Parameter>>)
                CompressorCompat.BatchesItem1.GetValue(batchesObj);
            var boolBatches = (List<List<VRCExpressionParameters.Parameter>>)
                CompressorCompat.BatchesItem2.GetValue(batchesObj);
            var batchCount = Math.Max(numberBatches.Count, boolBatches.Count);
            var indexBitCount = (int)CompressorCompat.DecisionGetIndexBitCount.Invoke(decision, null);

            // Params synced by batches later than the first / earlier than the last get
            // latch entries on the first send state / last receive state, in that order —
            // needed to locate our copy entries positionally.
            int CountIn(IEnumerable<List<VRCExpressionParameters.Parameter>> batches, Func<int, bool> which) {
                return batches.Where((batch, num) => which(num)).Sum(batch => batch.Count);
            }
            var latchSendCount = CountIn(numberBatches, num => num != 0) + CountIn(boolBatches, num => num != 0);

            var compressorMachine = FindCompressorMachine(fxRaw);
            var trueParam = FindTrueParam(fxRaw);

            var plans = new List<PairPlan>();
            foreach (var (rep, partner) in pairs) {
                var located = false;
                for (var batchNum = 0; batchNum < numberBatches.Count && !located; batchNum++) {
                    var slotNum = numberBatches[batchNum].IndexOf(rep);
                    if (slotNum < 0) continue;
                    located = true;
                    plans.Add(BuildPlan(
                        compressorMachine, rep, partner, batchNum, slotNum,
                        numberBatches[batchNum].Count, batchCount, indexBitCount, latchSendCount));
                }
                if (!located) {
                    throw new Exception(
                        $"FuryPlusPlus sub-8-bit packing: '{rep.name}' not found in any compressor batch. " +
                        "Remove it from the precision list or disable the module.");
                }
            }

            // ---- mutate: params, driver entries, interstitial states, encode/decode layers ----
            foreach (var plan in plans) {
                var packedDefault = QuantizeIndex(plan.Rep.defaultValue) * 16
                                    + QuantizeIndex(plan.Partner.defaultValue);
                plan.PackedAap = MakeAap(fx, $"FPP/Sub8/{plan.Rep.name}+{plan.Partner.name}", packedDefault);
                plan.HiOutAap = MakeAap(fx, $"FPP/Sub8/{plan.Rep.name}/decoded",
                    DecodeValue(QuantizeIndex(plan.Rep.defaultValue)));
                plan.LoOutAap = MakeAap(fx, $"FPP/Sub8/{plan.Partner.name}/decoded",
                    DecodeValue(QuantizeIndex(plan.Partner.defaultValue)));

                // Send: same slot, packed AAP instead of the stock full-precision mapping.
                plan.SendEntry.source = plan.PackedAap;
                plan.SendEntry.convertRange = false;

                // Receive: drop the stock copy; the interstitial state applies decoded values.
                RemoveEntry(plan.ReceiveDriver, plan.ReceiveEntry);
                if (plan.UnlatchDriver != null) {
                    RemoveEntry(plan.UnlatchDriver, plan.UnlatchEntry);
                }
            }

            InsertDecodeStates(compressorMachine, plans, trueParam);

            var oneParam = GetOneParamName(fx);
            BuildEncodeLayer(fx, plans, oneParam);
            BuildDecodeLayer(fx, fxRaw, compressorMachine, plans, oneParam);
        }

        private static PairPlan BuildPlan(
            AnimatorStateMachine machine,
            VRCExpressionParameters.Parameter rep,
            VRCExpressionParameters.Parameter partner,
            int batchNum,
            int slotNum,
            int numbersInBatch,
            int batchCount,
            int indexBitCount,
            int latchSendCount
        ) {
            var titleId = TitleId(batchNum, indexBitCount);
            var sendState = FindState(machine, titleId, receive: false);
            var receiveState = FindState(machine, titleId, receive: true);

            var sendDriver = SingleDriver(sendState);
            var sendCopies = sendDriver.parameters
                .Where(entry => entry.type == VRC_AvatarParameterDriver.ChangeType.Copy).ToList();
            var sendSkip = batchNum == 0 ? latchSendCount : 0;
            var sendEntry = sendCopies.ElementAtOrDefault(sendSkip + slotNum);
            if (sendEntry == null || !sendEntry.name.Contains($"SyncDataNum{slotNum}")) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: send copy for '{rep.name}' " +
                                    $"(batch {batchNum}, slot {slotNum}) does not match the expected layout.");
            }

            var receiveDriver = SingleDriver(receiveState);
            var receiveCopies = receiveDriver.parameters
                .Where(entry => entry.type == VRC_AvatarParameterDriver.ChangeType.Copy).ToList();
            var receiveEntry = receiveCopies.ElementAtOrDefault(slotNum);
            if (receiveEntry == null || !receiveEntry.source.Contains($"SyncDataNum{slotNum}")) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: receive copy for '{rep.name}' " +
                                    $"(batch {batchNum}, slot {slotNum}) does not match the expected layout.");
            }

            var plan = new PairPlan {
                Rep = rep,
                Partner = partner,
                BatchNum = batchNum,
                SendDriver = sendDriver,
                SendEntry = sendEntry,
                ReceiveDriver = receiveDriver,
                ReceiveEntry = receiveEntry,
                SlotParamName = receiveEntry.source
            };

            var unlatchNow = batchNum == batchCount - 1;
            if (unlatchNow) {
                if (receiveEntry.name != rep.name) {
                    throw new Exception($"FuryPlusPlus sub-8-bit packing: expected final-batch receive of " +
                                        $"'{rep.name}' to unlatch directly, found '{receiveEntry.name}'.");
                }
            } else {
                // The receive wrote into a latch param; the last batch's receive state
                // copies latch → original. That copy must go too, or it would overwrite
                // our decoded value with the (never-written) latch default at cycle end.
                var latchName = receiveEntry.name;
                var unlatchState = FindState(machine, TitleId(batchCount - 1, indexBitCount), receive: true);
                var unlatchDriver = SingleDriver(unlatchState);
                var unlatchEntry = unlatchDriver.parameters.FirstOrDefault(entry =>
                    entry.type == VRC_AvatarParameterDriver.ChangeType.Copy
                    && entry.source == latchName && entry.name == rep.name);
                if (unlatchEntry == null) {
                    throw new Exception($"FuryPlusPlus sub-8-bit packing: unlatch copy for '{rep.name}' " +
                                        "not found on the final receive state.");
                }
                plan.UnlatchDriver = unlatchDriver;
                plan.UnlatchEntry = unlatchEntry;
            }
            return plan;
        }

        // ---- compressor layer surgery helpers ----

        private static AnimatorStateMachine FindCompressorMachine(AnimatorController fxRaw) {
            var layer = fxRaw.layers.LastOrDefault(l =>
                l.name != null && l.name.EndsWith("Parameter Compressor") && l.stateMachine != null);
            if (layer == null) {
                throw new Exception("FuryPlusPlus sub-8-bit packing: Parameter Compressor layer not found.");
            }
            return layer.stateMachine;
        }

        private static readonly Regex TrueParamName = new Regex(@"^VF_\d+_True$");

        private static string FindTrueParam(AnimatorController fxRaw) {
            var parameter = fxRaw.parameters.FirstOrDefault(p =>
                TrueParamName.IsMatch(p.name) && p.defaultBool);
            if (parameter == null) {
                throw new Exception("FuryPlusPlus sub-8-bit packing: always-true parameter not found.");
            }
            return parameter.name;
        }

        private static string TitleId(int batchNum, int indexBitCount) {
            var syncId = batchNum + 1;
            return string.Concat(Enumerable.Range(0, indexBitCount)
                .Select(i => (syncId & (1 << (indexBitCount - 1 - i))) > 0 ? "1" : "0"));
        }

        private static AnimatorState FindState(AnimatorStateMachine machine, string titleId, bool receive) {
            var marker = $"({titleId}):";
            var matches = machine.states
                .Select(child => child.state)
                .Where(state => state != null && state.name.Contains(marker))
                .Where(state => receive ? state.name.StartsWith("Receive") : state.name.Contains("Send"))
                .Where(state => !state.name.StartsWith("FPP"))
                .ToList();
            if (matches.Count != 1) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: expected exactly one " +
                                    $"{(receive ? "receive" : "send")} state for sync id {titleId}, " +
                                    $"found {matches.Count}.");
            }
            return matches[0];
        }

        private static VRCAvatarParameterDriver SingleDriver(AnimatorState state) {
            var drivers = state.behaviours.OfType<VRCAvatarParameterDriver>().ToList();
            if (drivers.Count != 1) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: expected one parameter driver on " +
                                    $"'{state.name}', found {drivers.Count}.");
            }
            return drivers[0];
        }

        private static void RemoveEntry(VRCAvatarParameterDriver driver, VRC_AvatarParameterDriver.Parameter entry) {
            driver.parameters = driver.parameters.Where(existing => existing != entry).ToList();
        }

        /**
         * Adds one interstitial state after each affected batch's receive state. The
         * receive state's outgoing transitions move onto the interstitial; the receive
         * state unconditionally advances there after one frame, at which point the decode
         * layer (which evaluates before this one) has processed the new slot value.
         */
        private static void InsertDecodeStates(
            AnimatorStateMachine machine,
            List<PairPlan> plans,
            string trueParam
        ) {
            foreach (var group in plans.GroupBy(plan => plan.BatchNum)) {
                var receiveState = group.First().ReceiveDriver == null
                    ? null
                    : machine.states.Select(c => c.state)
                        .FirstOrDefault(state => state != null
                                                 && state.behaviours.Contains(group.First().ReceiveDriver));
                if (receiveState == null) {
                    throw new Exception("FuryPlusPlus sub-8-bit packing: receive state lookup failed.");
                }

                var decodeState = Create<AnimatorState>();
                decodeState.name = $"FPP Apply ({group.Key})";
                decodeState.writeDefaultValues = receiveState.writeDefaultValues;
                decodeState.motion = null;
                machine.states = machine.states
                    .Concat(new[] { new ChildAnimatorState { state = decodeState } })
                    .ToArray();

                var driver = Create<VRCAvatarParameterDriver>();
                driver.localOnly = false;
                foreach (var plan in group) {
                    driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter {
                        type = VRC_AvatarParameterDriver.ChangeType.Copy,
                        source = plan.HiOutAap,
                        name = plan.Rep.name
                    });
                    driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter {
                        type = VRC_AvatarParameterDriver.ChangeType.Copy,
                        source = plan.LoOutAap,
                        name = plan.Partner.name
                    });
                }
                decodeState.behaviours = new StateMachineBehaviour[] { driver };

                // Move the receive state's outgoing transitions to the interstitial.
                var moved = receiveState.transitions;
                var advance = Create<AnimatorStateTransition>();
                advance.hasFixedDuration = true;
                advance.destinationState = decodeState;
                advance.hasExitTime = false;
                advance.duration = 0;
                advance.AddCondition(AnimatorConditionMode.If, 0, trueParam);
                receiveState.transitions = new[] { advance };
                decodeState.transitions = moved;
            }
        }

        // ---- quantization ----

        // 16 plateaus across -1..1; boundary k is where the quantized index switches to k.
        private static float Boundary(int k) {
            return (k - 0.5f) / 7.5f - 1f;
        }

        internal static int QuantizeIndex(float value) {
            var index = (int)Math.Round((Mathf.Clamp(value, -1f, 1f) + 1f) / 2f * 15f,
                MidpointRounding.AwayFromZero);
            return Mathf.Clamp(index, 0, 15);
        }

        internal static float DecodeValue(int index) {
            return index / 15f * 2f - 1f;
        }

        // ---- encode / decode construction (raw Unity objects: immune to VRCFury's
        //      factory prune, and serialized with the controller like stock's states) ----

        private static string MakeAap(object fx, string name, float def) {
            var aap = CompressorCompat.MakeAap.Invoke(fx, new object[] { name, def, true });
            return (string)vfaApName.Invoke(aap, null);
        }

        private static string GetOneParamName(object fx) {
            var one = controllerManagerOne.Invoke(fx, null);
            return (string)ToggleTreeCompat.VfaParamName.Invoke(one, null);
        }

        private static AnimationClip AapClip(string name, params (string Param, float Value)[] curves) {
            var clip = Create<AnimationClip>();
            clip.name = name;
            foreach (var (param, value) in curves) {
                // Through VRCFury's clip ext-db so the post-compressor SaveAssets finalizes it.
                var curve = CompressorCompat.FloatToCurve.Invoke(null, new object[] { value });
                CompressorCompat.ClipSetAap.Invoke(null, new object[] { clip, param, curve });
            }
            return clip;
        }

        private static BlendTree NewTree(string name, BlendTreeType type, string blendParam) {
            var tree = Create<BlendTree>();
            tree.name = name;
            tree.blendType = type;
            tree.useAutomaticThresholds = false;
            if (blendParam != null) tree.blendParameter = blendParam;
            return tree;
        }

        private static void AddChild(BlendTree tree, Motion motion, float threshold, string directParam) {
            var children = tree.children;
            var child = new ChildMotion { motion = motion, timeScale = 1, threshold = threshold };
            if (directParam != null) child.directBlendParameter = directParam;
            ArrayUtility.Add(ref children, child);
            tree.children = children;
        }

        /** 32-child plateau step tree: quantizes source into contribution*index on the AAP. */
        private static BlendTree StepTree(string sourceParam, string aap, float contributionPerIndex) {
            var tree = NewTree($"FPP Sub8 quantize {sourceParam}", BlendTreeType.Simple1D, sourceParam);
            const float epsilon = 1e-4f;
            for (var k = 0; k <= 15; k++) {
                var clip = AapClip($"{aap} = {k}", (aap, k * contributionPerIndex));
                var start = k == 0 ? -1f : Boundary(k);
                var end = k == 15 ? 1f : Boundary(k + 1) - epsilon;
                AddChild(tree, clip, start, null);
                AddChild(tree, clip, end, null);
            }
            return tree;
        }

        private static object NewDbtLayer(object fx, string name, int insertAt) {
            var layer = ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.NewLayer, fx, new object[] { name, insertAt });
            var state = ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.NewState, layer, new object[] { "DBT" });
            var root = NewTree("DBT", BlendTreeType.Direct, null);
            ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.StateWithAnimation, state, new object[] { (Motion)root });
            return root;
        }

        private static void BuildEncodeLayer(object fx, List<PairPlan> plans, string oneParam) {
            var root = (BlendTree)NewDbtLayer(fx, "FuryPlusPlus Sub8 Encode", -1);
            foreach (var plan in plans) {
                // Two step trees summing into the packed AAP: hi*16 + lo.
                AddChild(root, StepTree(plan.Rep.name, plan.PackedAap, 16f), 0, oneParam);
                AddChild(root, StepTree(plan.Partner.name, plan.PackedAap, 1f), 0, oneParam);
            }
        }

        private static void BuildDecodeLayer(
            object fx,
            AnimatorController fxRaw,
            AnimatorStateMachine compressorMachine,
            List<PairPlan> plans,
            string oneParam
        ) {
            // Must evaluate BEFORE the compressor layer so decoded AAPs are fresh by the
            // time the interstitial state's driver fires (one frame after slot arrival).
            var compressorIndex = Array.FindIndex(fxRaw.layers,
                layer => layer.stateMachine == compressorMachine);
            if (compressorIndex < 0) compressorIndex = fxRaw.layers.Length;

            var root = (BlendTree)NewDbtLayer(fx, "FuryPlusPlus Sub8 Decode", compressorIndex);
            foreach (var plan in plans) {
                var decode = NewTree($"FPP Sub8 decode {plan.SlotParamName}",
                    BlendTreeType.Simple1D, plan.SlotParamName);
                for (var value = 0; value <= 255; value++) {
                    var clip = AapClip(
                        $"decode {value}",
                        (plan.HiOutAap, DecodeValue(value >> 4)),
                        (plan.LoOutAap, DecodeValue(value & 15))
                    );
                    AddChild(decode, clip, value, null);
                }
                AddChild(root, decode, 0, oneParam);
            }
        }
    }
}
