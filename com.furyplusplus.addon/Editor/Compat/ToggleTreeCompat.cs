using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for the VRCFury members shared by the two toggle→blendtree
     * conversion modules (ToggleSeparateLocalModule / ToggleFadeModule). First
     * EnsureResolved pays; consuming modules Demand what they need in Install()
     * (fail-closed). Also carries small invocation wrappers so the pass bodies
     * never touch reflection primitives directly.
     */
    internal static class ToggleTreeCompat {
        private static bool resolved;

        // Layer / controller plumbing
        internal static MethodInfo GetFx;                      // ControllersService.GetFx()
        internal static MethodInfo GetLayers;                  // VFController.GetLayers()
        internal static MethodInfo GetRaw;                     // VFController.GetRaw() → AnimatorController
        internal static MethodInfo NewLayer;                   // ControllerManager.NewLayer(string, int)
        internal static MethodInfo NewState;                   // VFLayer.NewState(string)
        internal static MethodInfo StateWithAnimation;         // VFState.WithAnimation(Motion)
        internal static MethodInfo LayerRemove;                // VFLayer.Remove()
        internal static MethodInfo LayerGetId;                 // VFLayer.GetLayerId()
        internal static PropertyInfo LayerWeight;              // VFLayer.weight
        internal static PropertyInfo LayerName;                // VFLayer.name
        internal static PropertyInfo LayerBlendingMode;        // VFLayer.blendingMode
        internal static MethodInfo GetBindingsAnimatedInLayer; // LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)
        internal static MethodInfo IsLayerTargeted;            // AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)
        internal static MethodInfo GetDefaultLayer;            // FixWriteDefaultsService.GetDefaultLayer()

        // Motion helpers
        internal static MethodInfo MotionIsStatic;             // MotionExtensions.IsStatic(Motion)
        internal static MethodInfo MotionGetLastFrame;         // MotionExtensions.GetLastFrame(Motion, bool)
        internal static MethodInfo HasValidBinding;            // ValidateBindingsService.HasValidBinding(Motion)
        internal static MethodInfo ClipGetAllBindings;         // AnimationClipExtensions.GetAllBindings(clip)
        private static MethodInfo createAnimationClip;         // VrcfObjectFactory.Create<AnimationClip>(Object)
        internal static Type ClipsIteratorType;                // AnimatorIterator+Clips
        internal static MethodInfo ClipsFromMotion;            // AnimatorIterator.Clips.From(Motion)

        // Tree construction
        internal static MethodInfo DirectCreate;               // VFBlendTreeDirect.Create(string) static
        internal static MethodInfo DirectAddWeighted;          // VFBlendTreeDirect.Add(string, Motion)
        internal static MethodInfo DirectAddOne;               // VFBlendTreeDirect.Add(Motion)
        internal static MethodInfo Tree1DCreate;               // VFBlendTree1D.Create(string, string) static
        internal static MethodInfo Tree1DAdd;                  // VFBlendTree1D.Add(float, Motion)
        internal static FieldInfo BlendTreeField;              // VFBlendTree.tree (the raw BlendTree)

        // BlendtreeMath.Equals(VFAFloat, float, string, float) → VFAFloatBool { create }
        internal static MethodInfo MathEquals;
        internal static PropertyInfo FloatBoolCreate;
        internal static ConstructorInfo VfaFloatCtor;          // VFAFloat(string, float)
        internal static MethodInfo VfaParamName;               // VFAParam.Name()

        // Smoothing (fade module only)
        internal static MethodInfo Smooth;                     // SmoothingService.Smooth(tree,name,target,seconds,accel,min,max)

        // VRC built-in globals that may legitimately exceed 1 as ints (mirror of stock guard)
        internal static ISet<string> VrchatGlobalParams;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            VfLayerCompat.EnsureResolved();

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            var controllerManagerType = ReflectionUtils.FindType("VF.Utils.ControllerManager");
            var layerType = ReflectionUtils.FindType("VF.Utils.Controller.VFLayer");
            var stateType = ReflectionUtils.FindType("VF.Utils.Controller.VFState");
            var layerToTreeType = ReflectionUtils.FindType("VF.Service.LayerToTreeService");
            var layerControlType = ReflectionUtils.FindType("VF.Service.AnimatorLayerControlOffsetService");
            var fixWdType = ReflectionUtils.FindType("VF.Service.FixWriteDefaultsService");
            var motionExtType = ReflectionUtils.FindType("VF.Utils.MotionExtensions");
            var validateType = ReflectionUtils.FindType("VF.Service.ValidateBindingsService");
            var clipExtType = ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions");
            var factoryType = ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory");
            var directType = ReflectionUtils.FindType("VF.Utils.VFBlendTreeDirect");
            var tree1DType = ReflectionUtils.FindType("VF.Utils.VFBlendTree1D");
            var treeBaseType = ReflectionUtils.FindType("VF.Utils.VFBlendTree");
            var mathType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath");
            var floatBoolType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath+VFAFloatBool");
            var vfaFloatType = ReflectionUtils.FindType("VF.Utils.Controller.VFAFloat");
            var vfaParamType = ReflectionUtils.FindType("VF.Utils.Controller.VFAParam");
            var smoothingType = ReflectionUtils.FindType("VF.Service.SmoothingService");
            var fullControllerType = ReflectionUtils.FindType("VF.Feature.FullControllerBuilder");

            if (controllersServiceType == null || vfControllerType == null || layerType == null) return;

            GetFx = ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetFx",
                method => method.GetParameters().Length == 0);
            GetLayers = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                method => method.GetParameters().Length == 0);
            GetRaw = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetRaw",
                method => method.GetParameters().Length == 0);
            NewLayer = controllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                controllerManagerType, "NewLayer", method => method.GetParameters().Length == 2);
            NewState = ReflectionUtils.FindUniqueMethod(layerType, "NewState",
                method => method.GetParameters().Length == 1);
            StateWithAnimation = stateType == null ? null : ReflectionUtils.FindUniqueMethod(
                stateType, "WithAnimation", method => method.GetParameters().Length == 1);
            LayerRemove = ReflectionUtils.FindNoArgVoid(layerType, "Remove");
            LayerGetId = ReflectionUtils.FindUniqueMethod(layerType, "GetLayerId",
                method => method.GetParameters().Length == 0);
            LayerWeight = layerType.GetProperty("weight", any);
            LayerName = layerType.GetProperty("name", any);
            LayerBlendingMode = layerType.GetProperty("blendingMode", any);
            GetBindingsAnimatedInLayer = layerToTreeType == null ? null : ReflectionUtils.FindUniqueMethod(
                layerToTreeType, "GetBindingsAnimatedInLayer", method => method.GetParameters().Length == 1);
            IsLayerTargeted = layerControlType == null ? null : ReflectionUtils.FindUniqueMethod(
                layerControlType, "IsLayerTargeted", method => method.GetParameters().Length == 1);
            GetDefaultLayer = fixWdType == null ? null : ReflectionUtils.FindUniqueMethod(
                fixWdType, "GetDefaultLayer", method => method.GetParameters().Length == 0);

            MotionIsStatic = motionExtType == null ? null : ReflectionUtils.FindUniqueMethod(
                motionExtType, "IsStatic", method => method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(Motion));
            MotionGetLastFrame = motionExtType == null ? null : ReflectionUtils.FindUniqueMethod(
                motionExtType, "GetLastFrame", method => method.GetParameters().Length == 2
                    && method.GetParameters()[0].ParameterType == typeof(Motion));
            HasValidBinding = validateType == null ? null : ReflectionUtils.FindUniqueMethod(
                validateType, "HasValidBinding", method => method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(Motion));
            ClipGetAllBindings = clipExtType == null ? null : ReflectionUtils.FindUniqueMethod(
                clipExtType, "GetAllBindings", method => method.GetParameters().Length == 1);
            if (factoryType != null) {
                var openCreate = factoryType
                    .GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .SingleOrDefault(method => method.Name == "Create"
                                               && method.IsGenericMethodDefinition
                                               && method.GetParameters().Length == 1);
                createAnimationClip = openCreate?.MakeGenericMethod(typeof(AnimationClip));
            }
            ClipsIteratorType = ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips");
            ClipsFromMotion = ClipsIteratorType == null ? null : ReflectionUtils.FindUniqueMethod(
                ClipsIteratorType, "From", method => method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(Motion));

            if (directType != null) {
                DirectCreate = ReflectionUtils.FindUniqueMethod(directType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 1);
                DirectAddWeighted = ReflectionUtils.FindUniqueMethod(directType, "Add",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(string));
                DirectAddOne = ReflectionUtils.FindUniqueMethod(directType, "Add",
                    method => method.GetParameters().Length == 1
                              && method.GetParameters()[0].ParameterType == typeof(Motion));
            }
            if (tree1DType != null) {
                Tree1DCreate = ReflectionUtils.FindUniqueMethod(tree1DType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 2);
                Tree1DAdd = ReflectionUtils.FindUniqueMethod(tree1DType, "Add",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(float));
            }
            BlendTreeField = treeBaseType?.GetField("tree", BindingFlags.Instance | BindingFlags.NonPublic);

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

        /** Demands every member both conversion modules share; call from Install(). */
        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(VfLayerCompat.RootStateMachineField, "VFLayer.rootStateMachine");
            ReflectionUtils.Demand(GetFx, "ControllersService.GetFx()");
            ReflectionUtils.Demand(GetLayers, "VFController.GetLayers()");
            ReflectionUtils.Demand(GetRaw, "VFController.GetRaw()");
            ReflectionUtils.Demand(NewLayer, "ControllerManager.NewLayer(string, int)");
            ReflectionUtils.Demand(NewState, "VFLayer.NewState(string)");
            ReflectionUtils.Demand(StateWithAnimation, "VFState.WithAnimation(Motion)");
            ReflectionUtils.Demand(LayerRemove, "VFLayer.Remove()");
            ReflectionUtils.Demand(LayerGetId, "VFLayer.GetLayerId()");
            ReflectionUtils.Demand(LayerWeight, "VFLayer.weight");
            ReflectionUtils.Demand(LayerName, "VFLayer.name");
            ReflectionUtils.Demand(LayerBlendingMode, "VFLayer.blendingMode");
            ReflectionUtils.Demand(GetBindingsAnimatedInLayer, "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            ReflectionUtils.Demand(IsLayerTargeted, "AnimatorLayerControlOffsetService.IsLayerTargeted(VFLayer)");
            ReflectionUtils.Demand(GetDefaultLayer, "FixWriteDefaultsService.GetDefaultLayer()");
            ReflectionUtils.Demand(MotionIsStatic, "MotionExtensions.IsStatic(Motion)");
            ReflectionUtils.Demand(MotionGetLastFrame, "MotionExtensions.GetLastFrame(Motion, bool)");
            ReflectionUtils.Demand(HasValidBinding, "ValidateBindingsService.HasValidBinding(Motion)");
            ReflectionUtils.Demand(ClipGetAllBindings, "AnimationClipExtensions.GetAllBindings(clip)");
            ReflectionUtils.Demand(createAnimationClip, "VrcfObjectFactory.Create<AnimationClip>()");
            ReflectionUtils.Demand(ClipsFromMotion, "AnimatorIterator.Clips.From(Motion)");
            ReflectionUtils.Demand(DirectCreate, "VFBlendTreeDirect.Create(string)");
            ReflectionUtils.Demand(DirectAddWeighted, "VFBlendTreeDirect.Add(string, Motion)");
            ReflectionUtils.Demand(DirectAddOne, "VFBlendTreeDirect.Add(Motion)");
            ReflectionUtils.Demand(Tree1DCreate, "VFBlendTree1D.Create(string, string)");
            ReflectionUtils.Demand(Tree1DAdd, "VFBlendTree1D.Add(float, Motion)");
            ReflectionUtils.Demand(BlendTreeField, "VFBlendTree.tree");
            ReflectionUtils.Demand(MathEquals, "BlendtreeMath.Equals(VFAFloat, float, string, float)");
            ReflectionUtils.Demand(FloatBoolCreate, "BlendtreeMath.VFAFloatBool.create");
            ReflectionUtils.Demand(VfaFloatCtor, "VFAFloat(string, float)");
            ReflectionUtils.Demand(VfaParamName, "VFAParam.Name()");
            ReflectionUtils.Demand(VrchatGlobalParams, "FullControllerBuilder.VRChatGlobalParams");
        }

        // ---- invocation wrappers ----

        internal static AnimationClip NewEmptyClip(string name) {
            var clip = (AnimationClip)createAnimationClip.Invoke(null, new object[] { null });
            clip.name = name;
            return clip;
        }

        internal static Motion TreeToMotion(object vfBlendTree) {
            return (Motion)BlendTreeField.GetValue(vfBlendTree);
        }

        internal static object MakeVfaFloat(string name, float def) {
            return VfaFloatCtor.Invoke(new object[] { name, def });
        }

        /** BlendtreeMath.Equals(param, threshold).create(whenTrue, whenFalse) */
        internal static Motion EqualsSelect(string param, float threshold, Motion whenTrue, Motion whenFalse) {
            var floatBool = MathEquals.Invoke(null, new[] {
                MakeVfaFloat(param, 0f), (object)threshold, null, (object)0f
            });
            var create = (Delegate)FloatBoolCreate.GetValue(floatBool);
            return (Motion)create.DynamicInvoke(whenTrue, whenFalse);
        }

        /** Replicates DbtLayerService.Create: a new end-of-stack layer holding one direct tree. */
        internal static object CreateDbtLayer(object fx, string name) {
            var layer = ReflectionUtils.InvokeUnwrapped(NewLayer, fx, new object[] { name, -1 });
            var tree = DirectCreate.Invoke(null, new object[] { "DBT" });
            var state = ReflectionUtils.InvokeUnwrapped(NewState, layer, new object[] { "DBT" });
            ReflectionUtils.InvokeUnwrapped(StateWithAnimation, state, new object[] { TreeToMotion(tree) });
            return tree;
        }
    }
}
