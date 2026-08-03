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
    internal sealed class CompressorSub8Module : Module<CompressorSub8Module> {
        /** Float params (wildcards) that opt in to 4-bit pair packing; empty = module inert. */
        internal static readonly ModuleListOption PrecisionList = new ModuleListOption(
            "precisionList", "Precision list (float params to 4-bit pack)",
            "Semicolon-separated wildcard patterns of float parameters to pack in pairs at " +
            "4-bit precision. 16 steps is visible on radials — list only params where that's " +
            "acceptable.");

        private static readonly ModuleListOption[] AllListOptions = { PrecisionList };

        internal override string Id => "compressorSub8";
        internal override string DisplayName => "Compressor: 4-bit float pairs (opt-in list)";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Parameter compressor (sync bits)";
        internal override string Description =>
            "Packs pairs of listed float parameters into one int sync lane at 4-bit precision " +
            "each (16 steps — visible on radials, so list only params where that's acceptable). " +
            "Inert until the precision list below is filled. Quest uploads of the same avatar " +
            "need FuryPlusPlus with the same list.";

        internal override IReadOnlyList<ModuleListOption> ListOptions => AllListOptions;

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return CompressorScope.ReportedSub8Pairs > 0
                ? ($"{CompressorScope.ReportedSub8Pairs} float pairs → 4 bits last bake",
                    CompressorScope.Sub8Stats)
                : ((string, string)?)null;
        }

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorScope.EnsureInstalled(harmony);
            Sub8Surgery.Resolve();
            harmony.Patch(
                CompressorCompat.LayerBuildLayer,
                postfix: new HarmonyMethod(typeof(CompressorSub8Module), nameof(BuildLayerPostfix))
            );
        }

        private static void BuildLayerPostfix(object __0, object __1) {
            if (!CompressorScope.RunActive || !CompressorScope.Sub8Active) return;
            Sub8Surgery.Run(__0, __1);
        }

        internal override string ReportStats() {
            return CompressorScope.Sub8Stats;
        }
    }

    internal static class Sub8Surgery {
        private static System.Reflection.MethodInfo controllerManagerOne; // ControllerManager.One() → VFAFloat
        private static System.Reflection.MethodInfo vfaApName;            // BlendtreeMath.VFAap.Name()
        private static System.Reflection.MethodInfo factoryCreate;       // VrcfObjectFactory.Create(Type, Object)
        // Closed over VRCAvatarParameterDriver — behaviour containers hand back VFBehaviour
        // wrappers, so every driver has to come out through the typed accessor.
        private static System.Reflection.MethodInfo driverBehavioursAdd;
        private static System.Reflection.MethodInfo driverBehavioursGet;

        internal static void Resolve() {
            CompressorCompat.DemandCore();
            ReflectionUtils.Demand(CompressorCompat.LayerBuildLayer,
                "ParameterCompressorLayerService.BuildLayer(decision, controller)");
            ReflectionUtils.Demand(CompressorCompat.ControllerManagerType,
                "VF.Utils.ControllerManager");
            ReflectionUtils.Demand(CompressorCompat.DecisionGetIndexBitCount,
                "OptimizationDecision.GetIndexBitCount()");
            ReflectionUtils.Demand(CompressorCompat.MakeAap, "ControllerManager.MakeAap(string, float, bool)");

            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.GetLayers, "VFController.GetLayers()");
            ReflectionUtils.Demand(ToggleTreeCompat.NewLayer, "ControllerManager.NewLayer(string, int)");
            ReflectionUtils.Demand(ToggleTreeCompat.NewState, "VFLayer.NewState(string)");
            ReflectionUtils.Demand(ToggleTreeCompat.StateWithAnimation, "VFState.WithAnimation(VFMotion)");
            ReflectionUtils.Demand(ToggleTreeCompat.LayerStateMachine, "VFLayer.stateMachine");
            ReflectionUtils.Demand(ToggleTreeCompat.LayerName, "VFLayer.name");
            ReflectionUtils.Demand(ToggleTreeCompat.LayerGetId, "VFLayer.GetLayerId()");
            ReflectionUtils.Demand(ToggleTreeCompat.SmStates, "VFStateMachine.states");
            ReflectionUtils.Demand(ToggleTreeCompat.StateName, "VFState.name");
            ReflectionUtils.Demand(ToggleTreeCompat.StateBehaviours, "VFState.behaviours");
            ReflectionUtils.Demand(ToggleTreeCompat.StateTransitions, "VFState.transitions");
            ReflectionUtils.Demand(ToggleTreeCompat.StateWriteDefaults, "VFState.writeDefaultValues");
            ReflectionUtils.Demand(ToggleTreeCompat.TransitionType, "VF.Utils.Controller.VFTransition");
            ReflectionUtils.Demand(ToggleTreeCompat.TrConditions, "VFTransitionBase.conditions");
            ReflectionUtils.Demand(ToggleTreeCompat.TrDestinationState, "VFTransitionBase.destinationState");
            ReflectionUtils.Demand(ToggleTreeCompat.TrHasExitTime, "VFTransition.hasExitTime");
            ReflectionUtils.Demand(ToggleTreeCompat.TrDuration, "VFTransition.duration");
            ReflectionUtils.Demand(ToggleTreeCompat.TrHasFixedDuration, "VFTransition.hasFixedDuration");
            ReflectionUtils.Demand(ToggleTreeCompat.BehavioursAdd, "VFBehaviourContainer.AddBehaviour<T>()");
            ReflectionUtils.Demand(ToggleTreeCompat.BehavioursGet, "VFBehaviourContainer.GetBehaviours<T>()");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeCreate, "VFTree.Create(name, type, param, paramY)");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeChildType, "VF.Utils.Controller.VFTreeChild");
            ReflectionUtils.Demand(ToggleTreeCompat.TreeAddChild, "VFTree.AddChild(child)");
            ReflectionUtils.Demand(ToggleTreeCompat.ChildMotion, "VFTreeChild.motion");
            ReflectionUtils.Demand(ToggleTreeCompat.ChildThreshold, "VFTreeChild.threshold");
            ReflectionUtils.Demand(ToggleTreeCompat.ChildDirectBlendParameter, "VFTreeChild.directBlendParameter");
            ReflectionUtils.Demand(ToggleTreeCompat.ControllerGetParam, "VFController.GetParam(string)");
            driverBehavioursAdd = ToggleTreeCompat.BehavioursAdd
                .MakeGenericMethod(typeof(VRCAvatarParameterDriver));
            driverBehavioursGet = ToggleTreeCompat.BehavioursGet
                .MakeGenericMethod(typeof(VRCAvatarParameterDriver));

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

        internal static void Run(object decision, object controller) {
            var compress = (IList<VRCExpressionParameters.Parameter>)
                CompressorCompat.DecisionCompress.GetValue(decision);
            var pairs = CompressorScope.ComputeSub8Pairs(compress);
            if (pairs.Count == 0) return;
            if ((bool)CompressorCompat.DecisionUseBadPriority.GetValue(decision)) return;

            // ---- gather everything first; throw (failing the build) before any mutation ----

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

            var compressorLayer = FindCompressorLayer(controller);
            var compressorMachine = ToggleTreeCompat.LayerStateMachine.GetValue(compressorLayer);
            if (compressorMachine == null) {
                throw new Exception("FuryPlusPlus sub-8-bit packing: Parameter Compressor layer has no states.");
            }
            var trueParam = FindTrueParam(controller);

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
                plan.PackedAap = MakeAap(controller, $"FPP/Sub8/{plan.Rep.name}+{plan.Partner.name}", packedDefault);
                plan.HiOutAap = MakeAap(controller, $"FPP/Sub8/{plan.Rep.name}/decoded",
                    DecodeValue(QuantizeIndex(plan.Rep.defaultValue)));
                plan.LoOutAap = MakeAap(controller, $"FPP/Sub8/{plan.Partner.name}/decoded",
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

            InsertDecodeStates(compressorLayer, compressorMachine, plans, trueParam);

            var oneParam = GetOneParamName(controller);
            BuildEncodeLayer(controller, plans, oneParam);
            BuildDecodeLayer(controller, compressorLayer, plans, oneParam);
        }

        private static PairPlan BuildPlan(
            object machine,
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

        /** The compressor's own VFLayer, found by name the same way the raw scan did. */
        private static object FindCompressorLayer(object fx) {
            object found = null;
            foreach (var layer in (IEnumerable)ToggleTreeCompat.GetLayers.Invoke(fx, null)) {
                var name = (string)ToggleTreeCompat.LayerName.GetValue(layer);
                if (name == null || !name.EndsWith("Parameter Compressor")) continue;
                if (ToggleTreeCompat.LayerStateMachine.GetValue(layer) == null) continue;
                found = layer; // last match wins, as before
            }
            if (found == null) {
                throw new Exception("FuryPlusPlus sub-8-bit packing: Parameter Compressor layer not found.");
            }
            return found;
        }

        private static string FindTrueParam(object fx) {
            var parameters = (AnimatorControllerParameter[])
                ToggleTreeCompat.ControllerParameters.GetValue(fx);
            var parameter = parameters.FirstOrDefault(p =>
                ToggleTreeCompat.AlwaysTrueParamName.IsMatch(p.name) && p.defaultBool);
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

        private static string StateName(object state) {
            return (string)ToggleTreeCompat.StateName.GetValue(state);
        }

        private static object FindState(object machine, string titleId, bool receive) {
            var marker = $"({titleId}):";
            var matches = ToggleConversionRuntime.StatesOf(machine)
                .Where(state => state != null && StateName(state) != null && StateName(state).Contains(marker))
                .Where(state => receive
                    ? StateName(state).StartsWith("Receive")
                    : StateName(state).Contains("Send"))
                .Where(state => !StateName(state).StartsWith("FPP"))
                .ToList();
            if (matches.Count != 1) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: expected exactly one " +
                                    $"{(receive ? "receive" : "send")} state for sync id {titleId}, " +
                                    $"found {matches.Count}.");
            }
            return matches[0];
        }

        /**
         * The state's one parameter driver. VFBehaviourContainer holds VFBehaviour wrappers,
         * not StateMachineBehaviours, so the driver has to come out through the typed
         * accessor — an `OfType<VRCAvatarParameterDriver>()` over the container would compile
         * and silently match nothing. The instance handed back is the wrapper's own copy, and
         * Save clones from it, so mutating it here does reach the built controller.
         */
        private static VRCAvatarParameterDriver SingleDriver(object state) {
            var container = ToggleTreeCompat.StateBehaviours.GetValue(state);
            var drivers = ((IEnumerable)driverBehavioursGet.Invoke(container, null))
                .Cast<VRCAvatarParameterDriver>().ToList();
            if (drivers.Count != 1) {
                throw new Exception($"FuryPlusPlus sub-8-bit packing: expected one parameter driver on " +
                                    $"'{StateName(state)}', found {drivers.Count}.");
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
            object compressorLayer,
            object machine,
            List<PairPlan> plans,
            string trueParam
        ) {
            foreach (var group in plans.GroupBy(plan => plan.BatchNum)) {
                var wanted = group.First().ReceiveDriver;
                object receiveState = null;
                if (wanted != null) {
                    foreach (var state in ToggleConversionRuntime.StatesOf(machine)) {
                        if (state == null) continue;
                        var container = ToggleTreeCompat.StateBehaviours.GetValue(state);
                        var hasIt = ((IEnumerable)driverBehavioursGet.Invoke(container, null))
                            .Cast<VRCAvatarParameterDriver>()
                            .Any(driver => ReferenceEquals(driver, wanted));
                        if (hasIt) { receiveState = state; break; }
                    }
                }
                if (receiveState == null) {
                    throw new Exception("FuryPlusPlus sub-8-bit packing: receive state lookup failed.");
                }

                // NewState appends to this layer's state machine (and positions it) — the
                // detached model has no ChildAnimatorState array to rebuild.
                var decodeState = ReflectionUtils.InvokeUnwrapped(
                    ToggleTreeCompat.NewState, compressorLayer, new object[] { $"FPP Apply ({group.Key})" });
                ToggleTreeCompat.StateWriteDefaults.SetValue(
                    decodeState, ToggleTreeCompat.StateWriteDefaults.GetValue(receiveState));

                var decodeContainer = ToggleTreeCompat.StateBehaviours.GetValue(decodeState);
                var addedWrapper = ReflectionUtils.InvokeUnwrapped(
                    driverBehavioursAdd, decodeContainer, new object[] { null });
                if (addedWrapper == null) {
                    throw new Exception("FuryPlusPlus sub-8-bit packing: could not add the decode driver.");
                }
                var driver = ((IEnumerable)driverBehavioursGet.Invoke(decodeContainer, null))
                    .Cast<VRCAvatarParameterDriver>().Single();
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

                // Move the receive state's outgoing transitions to the interstitial. Both
                // .transitions are live Lists, so this is edit-in-place rather than reassign.
                var receiveTransitions = (IList)ToggleTreeCompat.StateTransitions.GetValue(receiveState);
                var decodeTransitions = (IList)ToggleTreeCompat.StateTransitions.GetValue(decodeState);
                var moved = receiveTransitions.Cast<object>().ToList();
                receiveTransitions.Clear();
                foreach (var transition in moved) decodeTransitions.Add(transition);

                var advance = Activator.CreateInstance(
                    ToggleTreeCompat.TransitionType, nonPublic: true);
                ToggleTreeCompat.TrHasFixedDuration.SetValue(advance, true);
                ToggleTreeCompat.TrDestinationState.SetValue(advance, decodeState);
                ToggleTreeCompat.TrHasExitTime.SetValue(advance, false);
                ToggleTreeCompat.TrDuration.SetValue(advance, 0f);
                ToggleTreeCompat.TrConditions.SetValue(advance, new[] {
                    new AnimatorCondition {
                        mode = AnimatorConditionMode.If, threshold = 0, parameter = trueParam
                    }
                });
                receiveTransitions.Add(advance);
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

        private static string MakeAap(object controller, string name, float def) {
            var aap = CompressorCompat.MakeAap.Invoke(controller, new object[] { name, def, true });
            return (string)vfaApName.Invoke(aap, null);
        }

        private static string GetOneParamName(object controller) {
            var one = controllerManagerOne.Invoke(controller, null);
            return (string)ToggleTreeCompat.VfaParamName.Invoke(one, null);
        }

        /** An in-memory VFClip writing one or more AAPs at a constant value. */
        private static object AapClip(string name, params (string Param, float Value)[] curves) {
            var clip = ToggleTreeCompat.NewEmptyClip(name);
            foreach (var (param, value) in curves) {
                var curve = CompressorCompat.FloatToCurve.Invoke(null, new object[] { value });
                ReflectionUtils.InvokeUnwrapped(
                    CompressorCompat.ClipSetAap, clip, new object[] { param, curve });
            }
            return clip;
        }

        /** VFTree.Create already sets useAutomaticThresholds/normalizedBlendValues false. */
        private static object NewTree(string name, BlendTreeType type, string blendParam) {
            return ToggleTreeCompat.TreeCreate.Invoke(
                null, new object[] { name, type, blendParam, null });
        }

        private static object Child(object motion, float threshold, string directParam) {
            var child = Activator.CreateInstance(ToggleTreeCompat.TreeChildType);
            ToggleTreeCompat.ChildMotion.SetValue(child, motion);
            ToggleTreeCompat.ChildThreshold.SetValue(child, threshold);
            if (directParam != null) {
                ToggleTreeCompat.ChildDirectBlendParameter.SetValue(child, directParam);
            }
            return child;
        }

        private static void AddChild(object tree, object child) {
            ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.TreeAddChild, tree, new[] { child });
        }

        /** 32-child plateau step tree: quantizes source into contribution*index on the AAP. */
        private static object StepTree(string sourceParam, string aap, float contributionPerIndex) {
            var tree = NewTree($"FPP Sub8 quantize {sourceParam}", BlendTreeType.Simple1D, sourceParam);
            const float epsilon = 1e-4f;
            for (var k = 0; k <= 15; k++) {
                var clip = AapClip($"{aap} = {k}", (aap, k * contributionPerIndex));
                var start = k == 0 ? -1f : Boundary(k);
                var end = k == 15 ? 1f : Boundary(k + 1) - epsilon;
                AddChild(tree, Child(clip, start, null));
                AddChild(tree, Child(clip, end, null));
            }
            return tree;
        }

        private static object NewDbtLayer(object controller, string name, int insertAt) {
            var layer = ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.NewLayer, controller, new object[] { name, insertAt });
            var state = ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.NewState, layer, new object[] { "DBT" });
            var root = NewTree("DBT", BlendTreeType.Direct, null);
            ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.StateWithAnimation, state, new[] { root });
            return root;
        }

        private static void BuildEncodeLayer(object controller, List<PairPlan> plans, string oneParam) {
            var root = NewDbtLayer(controller, "FuryPlusPlus Sub8 Encode", -1);
            foreach (var plan in plans) {
                // Two step trees summing into the packed AAP: hi*16 + lo.
                AddChild(root, Child(StepTree(plan.Rep.name, plan.PackedAap, 16f), 0, oneParam));
                AddChild(root, Child(StepTree(plan.Partner.name, plan.PackedAap, 1f), 0, oneParam));
            }
        }

        private static void BuildDecodeLayer(
            object controller,
            object compressorLayer,
            List<PairPlan> plans,
            string oneParam
        ) {
            // Must evaluate BEFORE the compressor layer so decoded AAPs are fresh by the
            // time the interstitial state's driver fires (one frame after slot arrival).
            var compressorIndex = (int)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.LayerGetId, compressorLayer, null);
            if (compressorIndex < 0) compressorIndex = -1;

            var root = NewDbtLayer(controller, "FuryPlusPlus Sub8 Decode", compressorIndex);
            foreach (var plan in plans) {
                var decode = NewTree($"FPP Sub8 decode {plan.SlotParamName}",
                    BlendTreeType.Simple1D, plan.SlotParamName);
                for (var value = 0; value <= 255; value++) {
                    var clip = AapClip(
                        $"decode {value}",
                        (plan.HiOutAap, DecodeValue(value >> 4)),
                        (plan.LoOutAap, DecodeValue(value & 15))
                    );
                    AddChild(decode, Child(clip, value, null));
                }
                AddChild(root, Child(decode, 0, oneParam));
            }
        }
    }
}
