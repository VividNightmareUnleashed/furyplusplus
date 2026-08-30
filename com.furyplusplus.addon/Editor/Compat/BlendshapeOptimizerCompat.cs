using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /** VRCFury blendshape-optimizer members consumed by the replacement Apply body. */
    internal static class BlendshapeOptimizerCompat {
        private static bool resolved;
        private static FieldInfo bindingTupleItem1;
        private static FieldInfo bindingTupleItem2;
        private static FieldInfo controllerTupleItem2;

        internal static Type MmdCompatibilityType;
        internal static FieldInfo Globals;
        internal static FieldInfo AllFeatures;
        internal static FieldInfo AvatarObject;
        internal static FieldInfo Avatar;
        internal static FieldInfo Controllers;
        internal static FieldInfo Animators;
        internal static MethodInfo GetBlendshapeCurves;
        internal static MethodInfo GetAllUsedControllers;
        internal static MethodInfo GetSubControllers;
        internal static MethodInfo GetSkins;
        internal static MethodInfo SkinGetMesh;
        internal static MethodInfo SkinGetMutableMesh;
        internal static MethodInfo SkinOwner;
        internal static MethodInfo OwnerGetPath;
        internal static MethodInfo IsMaybeMmdBlendshape;
        internal static MethodInfo MeshDirty;
        internal static MethodInfo Apply;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var builderType = ReflectionUtils.FindType("VF.Feature.BlendshapeOptimizerBuilder");
            MmdCompatibilityType = ReflectionUtils.FindType("VF.Model.Feature.MmdCompatibility");
            var globalsType = ReflectionUtils.FindType("VF.Service.GlobalsService");
            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            var animatorsServiceType = ReflectionUtils.FindType("VF.Service.AnimatorHolderService");
            var vfGameObjectType = ReflectionUtils.FindType("VF.Utils.VFGameObject");
            var mmdUtilsType = ReflectionUtils.FindType("VF.Builder.MmdUtils");

            Globals = builderType?.GetField("globals", any);
            AllFeatures = globalsType?.GetField("allFeaturesInRun", any);
            AvatarObject = builderType?.GetField("avatarObject", any);
            Avatar = builderType?.GetField("avatar", any);
            Controllers = builderType?.GetField("controllers", any);
            Animators = builderType?.GetField("animators", any);
            GetBlendshapeCurves = ReflectionUtils.FindUniqueMethod(
                builderType, "GetBlendshapeCurves", method => method.GetParameters().Length == 1);
            GetAllUsedControllers = ReflectionUtils.FindUniqueMethod(
                controllersServiceType, "GetAllUsedControllers", method => method.GetParameters().Length == 0);
            GetSubControllers = ReflectionUtils.FindUniqueMethod(
                animatorsServiceType, "GetSubControllers", method => method.GetParameters().Length == 0);
            GetSkins = vfGameObjectType?
                .GetMethods(any)
                .SingleOrDefault(method => method.Name == "GetComponentsInSelfAndChildren"
                                           && method.IsGenericMethodDefinition
                                           && method.GetParameters().Length == 0)
                ?.MakeGenericMethod(typeof(SkinnedMeshRenderer));

            SkinGetMesh = FindExtension("VF.Utils.RendererExtensions", "GetMesh", 1)
                          ?? FindExtension("VF.Utils.SkinnedMeshRendererExtensions", "GetMesh", 1);
            SkinGetMutableMesh = FindExtension("VF.Utils.RendererExtensions", "GetMutableMesh", 2)
                                 ?? FindExtension(
                                     "VF.Utils.SkinnedMeshRendererExtensions", "GetMutableMesh", 2);
            SkinOwner = FindExtension("VF.Utils.VFGameObjectExtensions", "owner", 1);
            OwnerGetPath = vfGameObjectType?
                .GetMethods(any)
                .SingleOrDefault(method => {
                    var parameters = method.GetParameters();
                    return method.Name == "GetPath"
                           && parameters.Length == 3
                           && parameters[0].ParameterType == vfGameObjectType
                           && parameters[1].ParameterType == typeof(bool)
                           && parameters[2].ParameterType == typeof(bool);
                });
            IsMaybeMmdBlendshape = ReflectionUtils.FindUniqueMethod(
                mmdUtilsType, "IsMaybeMmdBlendshape", method => method.GetParameters().Length == 1);
            MeshDirty = FindExtension("VF.Utils.UnityCompatUtils", "Dirty", 1)
                        ?? FindExtension("VF.Utils.ObjectExtensions", "Dirty", 1);
            Apply = ReflectionUtils.FindNoArgVoid(builderType, "Apply");
        }

        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(MmdCompatibilityType, "VF.Model.Feature.MmdCompatibility");
            ReflectionUtils.Demand(Globals, "BlendshapeOptimizerBuilder.globals");
            ReflectionUtils.Demand(AllFeatures, "GlobalsService.allFeaturesInRun");
            ReflectionUtils.Demand(AvatarObject, "BlendshapeOptimizerBuilder.avatarObject");
            ReflectionUtils.Demand(Avatar, "BlendshapeOptimizerBuilder.avatar");
            ReflectionUtils.Demand(Controllers, "BlendshapeOptimizerBuilder.controllers");
            ReflectionUtils.Demand(Animators, "BlendshapeOptimizerBuilder.animators");
            ReflectionUtils.Demand(GetBlendshapeCurves,
                "BlendshapeOptimizerBuilder.GetBlendshapeCurves(controller)");
            ReflectionUtils.Demand(GetAllUsedControllers,
                "ControllersService.GetAllUsedControllers()");
            ReflectionUtils.Demand(GetSubControllers, "AnimatorHolderService.GetSubControllers()");
            ReflectionUtils.Demand(GetSkins,
                "VFGameObject.GetComponentsInSelfAndChildren<SkinnedMeshRenderer>()");
            ReflectionUtils.Demand(SkinGetMesh, "SkinnedMeshRenderer.GetMesh()");
            ReflectionUtils.Demand(SkinGetMutableMesh, "SkinnedMeshRenderer.GetMutableMesh(reason)");
            ReflectionUtils.Demand(SkinOwner, "Component.owner()");
            ReflectionUtils.Demand(OwnerGetPath,
                "VFGameObject.GetPath(root, prettyRoot, removeCloneFromRoot)");
            ReflectionUtils.Demand(IsMaybeMmdBlendshape, "MmdUtils.IsMaybeMmdBlendshape(name)");
            ReflectionUtils.Demand(Apply, "BlendshapeOptimizerBuilder.Apply()");
        }

        internal static object BindingOf(object entry) {
            EnsureTupleFields(entry, ref bindingTupleItem1, ref bindingTupleItem2);
            return bindingTupleItem1.GetValue(entry);
        }

        internal static AnimationCurve CurveOf(object entry) {
            EnsureTupleFields(entry, ref bindingTupleItem1, ref bindingTupleItem2);
            return bindingTupleItem2.GetValue(entry) as AnimationCurve;
        }

        internal static object ControllerOf(object pair) {
            if (controllerTupleItem2 == null) {
                controllerTupleItem2 = pair.GetType().GetField("Item2");
            }
            return ReflectionUtils.Demand(controllerTupleItem2,
                "AnimatorHolderService.GetSubControllers tuple Item2").GetValue(pair);
        }

        private static void EnsureTupleFields(object tuple, ref FieldInfo item1, ref FieldInfo item2) {
            if (item1 != null && item2 != null) return;
            var type = tuple.GetType();
            item1 = ReflectionUtils.Demand(type.GetField("Item1"), "tuple Item1");
            item2 = ReflectionUtils.Demand(type.GetField("Item2"), "tuple Item2");
        }

        private static MethodInfo FindExtension(string typeName, string methodName, int paramCount) {
            var type = ReflectionUtils.FindType(typeName);
            return type?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == methodName
                                          && method.GetParameters().Length == paramCount);
        }
    }
}
