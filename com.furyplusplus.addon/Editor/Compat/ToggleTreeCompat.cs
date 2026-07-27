using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for the VRCFury members shared by the toggle→blendtree conversion
     * modules (ToggleSeparateLocalModule / ToggleFadeModule / DbtConsolidationModule).
     * First EnsureResolved pays; consuming modules Demand what they need in Install()
     * (fail-closed). Also carries small invocation wrappers so the pass bodies never touch
     * reflection primitives directly.
     *
     * As of VRCFury 1.1382 there is no live AnimatorStateMachine during a build: layers,
     * state machines, states, transitions and motions are all detached VF* objects. Every
     * member here therefore travels as `object`, and the graph walk goes
     * VFLayer.stateMachine → VFStateMachine.states → VFState.transitions rather than through
     * UnityEditor.Animations types. AnimatorCondition is the one exception — it is a public
     * struct and VRCFury still stores conditions verbatim as AnimatorCondition[].
     */
    internal static class ToggleTreeCompat {
        private static bool resolved;

        // Layer / controller plumbing
        internal static MethodInfo GetFx;                      // ControllersService.GetFx()
        internal static MethodInfo GetLayers;                  // VFController.GetLayers()
        /**
         * VFController.GetRaw(), deleted in the detached-controller rewrite — there is no
         * live AnimatorController during a build any more. Kept as a slot so the one module
         * still written against the raw graph (CompressorSub8Module) fails closed with a
         * precise message from its own Install instead of failing to compile.
         */
        internal static MethodInfo GetRaw;
        internal static MethodInfo NewLayer;                   // ControllerManager.NewLayer(string, int)
        internal static MethodInfo NewState;                   // VFLayer.NewState(string)
        internal static MethodInfo StateWithAnimation;         // VFState.WithAnimation(VFMotion)
        internal static MethodInfo LayerRemove;                // VFLayer.Remove()
        internal static MethodInfo LayerGetId;                 // VFLayer.GetLayerId()
        internal static PropertyInfo LayerWeight;              // VFLayer.weight
        internal static PropertyInfo LayerName;                // VFLayer.name
        internal static PropertyInfo LayerBlendingMode;        // VFLayer.blendingMode
        internal static PropertyInfo LayerStateMachine;        // VFLayer.stateMachine
        internal static PropertyInfo LayerHasSubMachines;      // VFLayer.hasSubMachines
        internal static MethodInfo GetBindingsAnimatedInLayer; // LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)
        internal static MethodInfo IsLayerTargeted;            // AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)
        internal static MethodInfo GetDefaultLayer;            // FixWriteDefaultsService.GetDefaultLayer()

        // VFStateMachine
        internal static PropertyInfo SmStates;                 // .states → IReadOnlyList<VFState>
        internal static PropertyInfo SmDefaultState;           // .defaultState
        internal static PropertyInfo SmEntryTransitions;       // .entryTransitions
        internal static PropertyInfo SmAnyStateTransitions;    // .anyStateTransitions
        internal static PropertyInfo SmBehaviours;             // .behaviours (a List<VFBehaviour>)

        // VFState
        internal static PropertyInfo StateMotion;              // .motion → VFMotion
        internal static PropertyInfo StateTransitions;         // .transitions → List<VFTransition>
        internal static PropertyInfo StateBehaviours;          // .behaviours
        internal static PropertyInfo StateName;                // .name
        internal static PropertyInfo StateTimeParamActive;     // .timeParameterActive
        internal static PropertyInfo StateSpeedParamActive;    // .speedParameterActive

        // VFTransitionBase / VFTransition
        internal static PropertyInfo TrConditions;             // .conditions → AnimatorCondition[]
        internal static PropertyInfo TrDestinationState;       // .destinationState → VFState
        internal static PropertyInfo TrIsExit;                 // .isExit
        internal static PropertyInfo TrHasExitTime;            // .hasExitTime
        internal static PropertyInfo TrDuration;               // .duration

        // Motion helpers
        internal static Type MotionType;                       // VF.Utils.Controller.VFMotion
        internal static MethodInfo MotionIsStatic;             // VFMotion.IsStatic()
        internal static MethodInfo MotionEvaluate;             // VFMotion.EvaluateMotion(float)
        internal static MethodInfo HasValidBinding;            // ValidateBindingsService.HasValidBinding(VFMotion, VFGameObject)
        internal static FieldInfo LayerAvatarObject;           // LayerToTreeService.avatarObject
        internal static MethodInfo ControllerGetParam;         // VFController.GetParam(string)
        internal static PropertyInfo ControllerParameters;     // VFController.parameters
        internal static Type ClipsIteratorType;                // AnimatorIterator+Clips
        internal static MethodInfo ClipsFromMotion;            // AnimatorIterator.Clips.From(VFMotion)
        private static MethodInfo clipCreate;                  // VFClip.Create(string)

        // Tree construction
        internal static MethodInfo DirectCreate;               // VFBlendTreeDirect.Create(string) static
        internal static MethodInfo DirectAddWeighted;          // VFBlendTreeDirect.Add(string, VFMotion)
        internal static MethodInfo DirectAddOne;               // VFBlendTreeDirect.Add(VFMotion)
        internal static MethodInfo Tree1DCreate;               // VFBlendTree1D.Create(string, string) static
        internal static MethodInfo Tree1DAdd;                  // VFBlendTree1D.Add(float, VFMotion)
        internal static MethodInfo TreeToMotionOp;             // VFBlendTree op_Implicit → VFMotion

        // VFTree (a raw direct blendtree in the detached model) + its children
        internal static Type TreeType;                         // VF.Utils.Controller.VFTree
        internal static PropertyInfo TreeChildren;             // .children → IReadOnlyList<VFTreeChild>
        internal static MethodInfo TreeAddChild;               // .AddChild(VFTreeChild)
        internal static PropertyInfo TreeBlendType;            // .blendType
        internal static PropertyInfo TreeNormalizedBlendValues;// .NormalizedBlendValues
        internal static PropertyInfo TreeBlendParameter;       // .BlendParameter
        internal static PropertyInfo TreeBlendParameterY;      // .BlendParameterY
        internal static FieldInfo ChildMotion;                 // VFTreeChild.motion
        internal static FieldInfo ChildDirectBlendParameter;   // VFTreeChild.directBlendParameter
        internal static MethodInfo TreeCreate;                 // VFTree.Create(name, type, param, paramY)
        internal static Type TreeChildType;                    // VF.Utils.Controller.VFTreeChild
        internal static FieldInfo ChildThreshold;              // VFTreeChild.threshold

        // State / transition construction (compressor surgery)
        internal static PropertyInfo StateWriteDefaults;       // VFState.writeDefaultValues
        internal static Type TransitionType;                   // VF.Utils.Controller.VFTransition
        internal static PropertyInfo TrHasFixedDuration;       // VFTransition.hasFixedDuration
        internal static MethodInfo BehavioursAdd;              // VFBehaviourContainer.AddBehaviour<T>(init)
        internal static MethodInfo BehavioursGet;              // VFBehaviourContainer.GetBehaviours<T>()

        // BlendtreeMath.Equals(VFAFloat, float, string, float) → VFAFloatBool { create }
        internal static MethodInfo MathEquals;
        internal static PropertyInfo FloatBoolCreate;
        internal static ConstructorInfo VfaFloatCtor;          // VFAFloat(string, float)
        internal static MethodInfo VfaParamName;               // VFAParam.Name()

        // Smoothing (fade module only)
        internal static MethodInfo Smooth;                     // SmoothingService.Smooth(tree,name,target,seconds,accel,min,max)

        // VRC built-in globals that may legitimately exceed 1 as ints (mirror of stock guard)
        internal static ISet<string> VrchatGlobalParams;

        /** VRCFury's always-true bool parameter naming convention (VF_<n>_True). */
        internal static readonly System.Text.RegularExpressions.Regex AlwaysTrueParamName =
            new System.Text.RegularExpressions.Regex(@"^VF_\d+_True$");

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            var controllerManagerType = ReflectionUtils.FindType("VF.Utils.ControllerManager");
            var layerType = ReflectionUtils.FindType("VF.Utils.Controller.VFLayer");
            var stateMachineType = ReflectionUtils.FindType("VF.Utils.Controller.VFStateMachine");
            var stateType = ReflectionUtils.FindType("VF.Utils.Controller.VFState");
            var transitionBaseType = ReflectionUtils.FindType("VF.Utils.Controller.VFTransitionBase");
            var transitionType = ReflectionUtils.FindType("VF.Utils.Controller.VFTransition");
            var layerToTreeType = ReflectionUtils.FindType("VF.Service.LayerToTreeService");
            var layerControlType = ReflectionUtils.FindType("VF.Service.AnimatorLayerControlOffsetService");
            var fixWdType = ReflectionUtils.FindType("VF.Service.FixWriteDefaultsService");
            var validateType = ReflectionUtils.FindType("VF.Service.ValidateBindingsService");
            var clipType = ReflectionUtils.FindType("VF.Utils.Controller.VFClip");
            // The blendtree *builders* stayed in VF.Utils; only the model types (VFTree,
            // VFTreeChild, VFClip, …) moved under VF.Utils.Controller.
            var directType = ReflectionUtils.FindType("VF.Utils.VFBlendTreeDirect");
            var tree1DType = ReflectionUtils.FindType("VF.Utils.VFBlendTree1D");
            var treeBaseType = ReflectionUtils.FindType("VF.Utils.VFBlendTree");
            var mathType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath");
            var floatBoolType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath+VFAFloatBool");
            var vfaFloatType = ReflectionUtils.FindType("VF.Utils.Controller.VFAFloat");
            var vfaParamType = ReflectionUtils.FindType("VF.Utils.Controller.VFAParam");
            var smoothingType = ReflectionUtils.FindType("VF.Service.SmoothingService");
            var fullControllerType = ReflectionUtils.FindType("VF.Feature.FullControllerBuilder");

            MotionType = ReflectionUtils.FindType("VF.Utils.Controller.VFMotion");
            if (controllersServiceType == null || vfControllerType == null
                || layerType == null || MotionType == null) {
                return;
            }

            GetFx = ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetFx",
                method => method.GetParameters().Length == 0);
            GetLayers = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                method => method.GetParameters().Length == 0);
            // Expected to be null at this pin; see the field comment.
            GetRaw = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetRaw",
                method => method.GetParameters().Length == 0);
            NewLayer = controllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                controllerManagerType, "NewLayer", method => method.GetParameters().Length == 2);
            NewState = ReflectionUtils.FindUniqueMethod(layerType, "NewState",
                method => method.GetParameters().Length == 1);
            StateWithAnimation = stateType == null ? null : ReflectionUtils.FindUniqueMethod(
                stateType, "WithAnimation", method => method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == MotionType);
            LayerRemove = ReflectionUtils.FindNoArgVoid(layerType, "Remove");
            LayerGetId = ReflectionUtils.FindUniqueMethod(layerType, "GetLayerId",
                method => method.GetParameters().Length == 0);
            LayerWeight = layerType.GetProperty("weight", any);
            LayerName = layerType.GetProperty("name", any);
            LayerBlendingMode = layerType.GetProperty("blendingMode", any);
            LayerStateMachine = layerType.GetProperty("stateMachine", any);
            LayerHasSubMachines = layerType.GetProperty("hasSubMachines", any);
            GetBindingsAnimatedInLayer = layerToTreeType == null ? null : ReflectionUtils.FindUniqueMethod(
                layerToTreeType, "GetBindingsAnimatedInLayer", method => method.GetParameters().Length == 1);
            IsLayerTargeted = layerControlType == null ? null : ReflectionUtils.FindUniqueMethod(
                layerControlType, "IsLayerTargeted", method => method.GetParameters().Length == 1);
            GetDefaultLayer = fixWdType == null ? null : ReflectionUtils.FindUniqueMethod(
                fixWdType, "GetDefaultLayer", method => method.GetParameters().Length == 0);

            if (stateMachineType != null) {
                SmStates = stateMachineType.GetProperty("states", inst);
                SmDefaultState = stateMachineType.GetProperty("defaultState", inst);
                SmEntryTransitions = stateMachineType.GetProperty("entryTransitions", inst);
                SmAnyStateTransitions = stateMachineType.GetProperty("anyStateTransitions", inst);
                SmBehaviours = stateMachineType.GetProperty("behaviours", inst);
            }
            if (stateType != null) {
                StateMotion = stateType.GetProperty("motion", inst);
                StateTransitions = stateType.GetProperty("transitions", inst);
                StateBehaviours = stateType.GetProperty("behaviours", inst);
                StateName = stateType.GetProperty("name", inst);
                StateTimeParamActive = stateType.GetProperty("timeParameterActive", inst);
                StateSpeedParamActive = stateType.GetProperty("speedParameterActive", inst);
            }
            if (transitionBaseType != null) {
                TrConditions = transitionBaseType.GetProperty("conditions", inst);
                TrDestinationState = transitionBaseType.GetProperty("destinationState", inst);
                TrIsExit = transitionBaseType.GetProperty("isExit", inst);
            }
            TrHasExitTime = transitionType?.GetProperty("hasExitTime", inst);
            TrDuration = transitionType?.GetProperty("duration", inst);

            MotionIsStatic = ReflectionUtils.FindUniqueMethod(MotionType, "IsStatic",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                method => method.GetParameters().Length == 0);
            MotionEvaluate = ReflectionUtils.FindUniqueMethod(MotionType, "EvaluateMotion",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                method => method.GetParameters().Length == 1);
            HasValidBinding = validateType == null ? null : ReflectionUtils.FindUniqueMethod(
                validateType, "HasValidBinding", method => {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2
                           && parameters[0].ParameterType == MotionType
                           && parameters[1].ParameterType.FullName == "VF.Utils.VFGameObject";
                });
            LayerAvatarObject = layerToTreeType?.GetField("avatarObject", inst);
            clipCreate = clipType == null ? null : ReflectionUtils.FindUniqueMethod(
                clipType, "Create", method => method.IsStatic && method.GetParameters().Length == 1);
            ControllerGetParam = ReflectionUtils.FindMethodWithSignature(
                vfControllerType, "GetParam",
                typeof(UnityEngine.AnimatorControllerParameter), typeof(string));
            ControllerParameters = vfControllerType.GetProperty("parameters", inst);
            ClipsIteratorType = ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips");
            ClipsFromMotion = ClipsIteratorType == null ? null : ReflectionUtils.FindUniqueMethod(
                ClipsIteratorType, "From", method => method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == MotionType);

            if (directType != null) {
                DirectCreate = ReflectionUtils.FindUniqueMethod(directType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 1);
                DirectAddWeighted = ReflectionUtils.FindUniqueMethod(directType, "Add",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(string));
                DirectAddOne = ReflectionUtils.FindUniqueMethod(directType, "Add",
                    method => method.GetParameters().Length == 1
                              && method.GetParameters()[0].ParameterType == MotionType);
            }
            if (tree1DType != null) {
                Tree1DCreate = ReflectionUtils.FindUniqueMethod(tree1DType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 2);
                Tree1DAdd = ReflectionUtils.FindUniqueMethod(tree1DType, "Add",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(float));
            }
            // The blendtree wrappers are no longer a thin shell over a raw BlendTree field —
            // they convert to VFMotion through a public implicit operator.
            TreeToMotionOp = treeBaseType == null ? null : ReflectionUtils.FindUniqueMethod(
                treeBaseType, "op_Implicit",
                method => method.IsStatic
                          && method.ReturnType == MotionType
                          && method.GetParameters().Length == 1
                          && method.GetParameters()[0].ParameterType == treeBaseType);

            TreeType = ReflectionUtils.FindType("VF.Utils.Controller.VFTree");
            if (TreeType != null) {
                TreeChildren = TreeType.GetProperty("children", inst);
                TreeAddChild = ReflectionUtils.FindUniqueMethod(TreeType, "AddChild",
                    method => method.GetParameters().Length == 1);
                TreeBlendType = TreeType.GetProperty("blendType", inst);
                TreeNormalizedBlendValues = TreeType.GetProperty("NormalizedBlendValues", inst);
                TreeBlendParameter = TreeType.GetProperty("BlendParameter", inst);
                TreeBlendParameterY = TreeType.GetProperty("BlendParameterY", inst);
            }
            if (TreeType != null) {
                TreeCreate = ReflectionUtils.FindUniqueMethod(TreeType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 4);
            }
            TreeChildType = ReflectionUtils.FindType("VF.Utils.Controller.VFTreeChild");
            ChildMotion = TreeChildType?.GetField("motion", inst);
            ChildDirectBlendParameter = TreeChildType?.GetField("directBlendParameter", inst);
            ChildThreshold = TreeChildType?.GetField("threshold", inst);

            StateWriteDefaults = stateType?.GetProperty("writeDefaultValues", inst);
            TransitionType = ReflectionUtils.FindType("VF.Utils.Controller.VFTransition");
            TrHasFixedDuration = TransitionType?.GetProperty("hasFixedDuration", inst);
            var behaviourContainerType = ReflectionUtils.FindType("VF.Utils.Controller.VFBehaviourContainer");
            if (behaviourContainerType != null) {
                BehavioursAdd = ReflectionUtils.FindUniqueMethod(behaviourContainerType, "AddBehaviour",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.IsGenericMethodDefinition && method.GetParameters().Length == 1);
                BehavioursGet = ReflectionUtils.FindUniqueMethod(behaviourContainerType, "GetBehaviours",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
            }

            if (mathType != null && vfaFloatType != null) {
                MathEquals = ReflectionUtils.FindUniqueMethod(mathType, "Equals",
                    method => method.IsStatic && method.GetParameters().Length == 4
                              && method.GetParameters()[0].ParameterType == vfaFloatType);
                VfaFloatCtor = vfaFloatType.GetConstructor(new[] { typeof(string), typeof(float) });
            }
            FloatBoolCreate = floatBoolType?.GetProperty("create", any);
            VfaParamName = vfaParamType == null ? null : ReflectionUtils.FindUniqueMethod(
                vfaParamType, "Name", method => method.GetParameters().Length == 0);

            Smooth = smoothingType == null ? null : ReflectionUtils.FindUniqueMethod(
                smoothingType, "Smooth", method => method.GetParameters().Length == 7);

            var globalsField = fullControllerType?.GetField("VRChatGlobalParams",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (globalsField?.GetValue(null) is IEnumerable globals) {
                VrchatGlobalParams = new HashSet<string>(globals.OfType<string>());
            }
        }

        /** Demands every member the conversion modules share; call from Install(). */
        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(GetLayers, "VFController.GetLayers()");
            ReflectionUtils.Demand(NewLayer, "ControllerManager.NewLayer(string, int)");
            ReflectionUtils.Demand(NewState, "VFLayer.NewState(string)");
            ReflectionUtils.Demand(StateWithAnimation, "VFState.WithAnimation(VFMotion)");
            ReflectionUtils.Demand(LayerRemove, "VFLayer.Remove()");
            ReflectionUtils.Demand(LayerGetId, "VFLayer.GetLayerId()");
            ReflectionUtils.Demand(LayerWeight, "VFLayer.weight");
            ReflectionUtils.Demand(LayerName, "VFLayer.name");
            ReflectionUtils.Demand(LayerBlendingMode, "VFLayer.blendingMode");
            ReflectionUtils.Demand(LayerStateMachine, "VFLayer.stateMachine");
            ReflectionUtils.Demand(LayerHasSubMachines, "VFLayer.hasSubMachines");
            ReflectionUtils.Demand(GetBindingsAnimatedInLayer, "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            ReflectionUtils.Demand(IsLayerTargeted, "AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)");
            ReflectionUtils.Demand(GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");
            ReflectionUtils.Demand(SmStates, "VFStateMachine.states");
            ReflectionUtils.Demand(SmDefaultState, "VFStateMachine.defaultState");
            ReflectionUtils.Demand(SmEntryTransitions, "VFStateMachine.entryTransitions");
            ReflectionUtils.Demand(SmAnyStateTransitions, "VFStateMachine.anyStateTransitions");
            ReflectionUtils.Demand(SmBehaviours, "VFStateMachine.behaviours");
            ReflectionUtils.Demand(StateMotion, "VFState.motion");
            ReflectionUtils.Demand(StateTransitions, "VFState.transitions");
            ReflectionUtils.Demand(StateBehaviours, "VFState.behaviours");
            ReflectionUtils.Demand(StateTimeParamActive, "VFState.timeParameterActive");
            ReflectionUtils.Demand(StateSpeedParamActive, "VFState.speedParameterActive");
            ReflectionUtils.Demand(TrConditions, "VFTransitionBase.conditions");
            ReflectionUtils.Demand(TrDestinationState, "VFTransitionBase.destinationState");
            ReflectionUtils.Demand(TrIsExit, "VFTransitionBase.isExit");
            ReflectionUtils.Demand(TrHasExitTime, "VFTransition.hasExitTime");
            ReflectionUtils.Demand(TrDuration, "VFTransition.duration");
            ReflectionUtils.Demand(MotionIsStatic, "VFMotion.IsStatic()");
            ReflectionUtils.Demand(MotionEvaluate, "VFMotion.EvaluateMotion(fraction)");
            ReflectionUtils.Demand(
                HasValidBinding,
                "ValidateBindingsService.HasValidBinding(VFMotion, VFGameObject)");
            ReflectionUtils.Demand(LayerAvatarObject, "LayerToTreeService.avatarObject");
            ReflectionUtils.Demand(ControllerGetParam, "VFController.GetParam(string)");
            ReflectionUtils.Demand(ClipsFromMotion, "AnimatorIterator.Clips.From(VFMotion)");
            ReflectionUtils.Demand(clipCreate, "VFClip.Create(name)");
            ReflectionUtils.Demand(DirectCreate, "VFBlendTreeDirect.Create(string)");
            ReflectionUtils.Demand(DirectAddWeighted, "VFBlendTreeDirect.Add(string, VFMotion)");
            ReflectionUtils.Demand(DirectAddOne, "VFBlendTreeDirect.Add(VFMotion)");
            ReflectionUtils.Demand(Tree1DCreate, "VFBlendTree1D.Create(string, string)");
            ReflectionUtils.Demand(Tree1DAdd, "VFBlendTree1D.Add(float, VFMotion)");
            ReflectionUtils.Demand(TreeToMotionOp, "VFBlendTree op_Implicit(VFMotion)");
            ReflectionUtils.Demand(MathEquals, "BlendtreeMath.Equals(VFAFloat, float, string, float)");
            ReflectionUtils.Demand(FloatBoolCreate, "BlendtreeMath.VFAFloatBool.create");
            ReflectionUtils.Demand(VfaFloatCtor, "VFAFloat(string, float)");
            ReflectionUtils.Demand(VfaParamName, "VFAParam.Name()");
            ReflectionUtils.Demand(VrchatGlobalParams, "FullControllerBuilder.VRChatGlobalParams");
        }

        // ---- invocation wrappers ----

        /** An empty in-memory clip. Replaces VrcfObjectFactory.Create<AnimationClip>(). */
        internal static object NewEmptyClip(string name) {
            return clipCreate.Invoke(null, new object[] { name });
        }

        internal static object TreeToMotion(object vfBlendTree) {
            return TreeToMotionOp.Invoke(null, new[] { vfBlendTree });
        }

        internal static object GetBindingRoot(object layerToTreeService) {
            return LayerAvatarObject.GetValue(layerToTreeService);
        }

        internal static object MakeVfaFloat(string name, float def) {
            return VfaFloatCtor.Invoke(new object[] { name, def });
        }

        /** BlendtreeMath.Equals(param, threshold).create(whenTrue, whenFalse) */
        internal static object EqualsSelect(string param, float threshold, object whenTrue, object whenFalse) {
            var floatBool = MathEquals.Invoke(null, new[] {
                MakeVfaFloat(param, 0f), (object)threshold, null, (object)0f
            });
            var create = (Delegate)FloatBoolCreate.GetValue(floatBool);
            return create.DynamicInvoke(whenTrue, whenFalse);
        }

        /**
         * Replicates DbtLayerService.Create: a new end-of-stack layer holding one direct tree.
         * The service itself is [VFPrototypeScope] — it names the layer after whichever
         * builder asked for it — so it cannot be resolved from the injector and these four
         * lines are mirrored here instead.
         */
        internal static object CreateDbtLayer(object fx, string name) {
            var layer = ReflectionUtils.InvokeUnwrapped(NewLayer, fx, new object[] { name, -1 });
            var tree = DirectCreate.Invoke(null, new object[] { "DBT" });
            var state = ReflectionUtils.InvokeUnwrapped(NewState, layer, new object[] { "DBT" });
            ReflectionUtils.InvokeUnwrapped(StateWithAnimation, state, new[] { TreeToMotion(tree) });
            return tree;
        }
    }
}
