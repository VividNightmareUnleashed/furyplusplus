using System;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /// <summary>
    /// Resolves the VRCFury members shared by the armature-phase patches exactly once per
    /// domain load. A rename in VRCFury is then fixed here instead of in every patch, and
    /// patches that must agree on the same MethodInfo (one prefixes it, another invokes
    /// it) agree structurally.
    /// </summary>
    internal static class ArmatureCompat {
        internal static MethodInfo ArmatureLinkApply { get; private set; }
        internal static FieldInfo ArmatureLinkAvatarField { get; private set; }
        internal static MethodInfo HapticSocketsApply { get; private set; }
        internal static FieldInfo HapticSocketsAvatarField { get; private set; }
        internal static Type VfGameObjectType { get; private set; }
        internal static MethodInfo GetConstraintsMethod { get; private set; }
        internal static MethodInfo RemoveFromPhysbones { get; private set; }
        internal static Type ConstraintType { get; private set; }
        internal static MethodInfo ConstraintCreate { get; private set; }
        internal static MethodInfo ConstraintGetAffectedObject { get; private set; }
        internal static MethodInfo ConstraintGetComponent { get; private set; }
        internal static MethodInfo ConstraintDestroy { get; private set; }
        internal static Type PhysboneType { get; private set; }
        internal static FieldInfo PhysboneIgnoreTransforms { get; private set; }
        internal static MethodInfo PhysboneGetRootTransform { get; private set; }
        internal static MethodInfo VfDestroy { get; private set; }
        internal static MethodInfo VfGetUploadRoots { get; private set; }
        internal static MethodInfo RewriteSkins { get; private set; }
        internal static MethodInfo GetRootName { get; private set; }
        internal static MethodInfo GetMutableMesh { get; private set; }
        internal static MethodInfo Dirty { get; private set; }

        internal sealed class DestroyCategoryMembers {
            internal Type ComponentType;
            internal MethodInfo GetRootTransform;
        }
        internal static DestroyCategoryMembers[] DestroyCategories { get; private set; }

        private static bool resolved;

        internal static bool ArmatureLinkAvailable =>
            ArmatureLinkApply != null && ArmatureLinkAvatarField != null
            && VfGameObjectCompat.GameObjectField != null;

        internal static bool HapticSocketsAvailable =>
            HapticSocketsApply != null && HapticSocketsAvatarField != null
            && VfGameObjectCompat.GameObjectField != null;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            var armatureType = ReflectionUtils.FindType("VF.Service.ArmatureLinkService");
            ArmatureLinkApply = ReflectionUtils.FindNoArgVoid(armatureType, "Apply");
            ArmatureLinkAvatarField = armatureType?.GetField(
                "avatarObject",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            RewriteSkins = ReflectionUtils.FindUniqueMethod(
                armatureType, "RewriteSkins",
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 3);
            GetRootName = ReflectionUtils.FindUniqueMethod(
                armatureType, "GetRootName",
                method => method.ReturnType == typeof(string) && method.GetParameters().Length == 2);
            GetMutableMesh = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VF.Utils.RendererExtensions"),
                "GetMutableMesh", typeof(Mesh), typeof(Renderer), typeof(string));
            Dirty = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VF.Utils.DirtyUtils"),
                "Dirty", typeof(void), typeof(UnityEngine.Object));

            var hapticType = ReflectionUtils.FindType("VF.Service.BakeHapticSocketsService");
            HapticSocketsApply = ReflectionUtils.FindNoArgVoid(hapticType, "Apply");
            HapticSocketsAvatarField = hapticType?.GetField(
                "avatarObject",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            VfGameObjectCompat.EnsureResolved();
            VfGameObjectType = VfGameObjectCompat.VfGameObjectType;
            GetConstraintsMethod = ReflectionUtils.FindUniqueMethod(
                VfGameObjectType,
                "GetConstraints",
                method => {
                    if (!method.ReturnType.IsArray) return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2
                           && parameters[0].ParameterType == typeof(bool)
                           && parameters[1].ParameterType == typeof(bool);
                }
            );

            var physboneUtilsType = ReflectionUtils.FindType("VF.Utils.PhysboneUtils");
            RemoveFromPhysbones = ReflectionUtils.FindUniqueMethod(
                physboneUtilsType,
                "RemoveFromPhysbones",
                method => {
                    var parameters = method.GetParameters();
                    return method.ReturnType == typeof(void)
                           && parameters.Length == 2
                           && parameters[1].ParameterType == typeof(bool);
                }
            );

            ConstraintType = ReflectionUtils.FindType("VF.Utils.VFConstraint");
            ConstraintCreate = ConstraintType?.GetMethod(
                "CreateOrNull",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Component) },
                null
            );
            ConstraintGetAffectedObject = ReflectionUtils.FindUniqueMethod(
                ConstraintType, "GetAffectedObject", method => method.GetParameters().Length == 0);
            ConstraintGetComponent = ReflectionUtils.FindUniqueMethod(
                ConstraintType, "GetComponent", method => method.GetParameters().Length == 0);
            ConstraintDestroy = ReflectionUtils.FindNoArgVoid(ConstraintType, "Destroy");

            PhysboneType = ReflectionUtils.FindType(
                "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            var physboneBaseType = ReflectionUtils.FindType("VRC.Dynamics.VRCPhysBoneBase");
            PhysboneIgnoreTransforms = physboneBaseType?.GetField(
                "ignoreTransforms",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            PhysboneGetRootTransform = physboneBaseType?.GetMethod(
                "GetRootTransform",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null
            );

            VfDestroy = ReflectionUtils.FindNoArgVoid(VfGameObjectType, "Destroy");
            VfGetUploadRoots = VfGameObjectType?
                .GetProperty("uploadRoots",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetGetMethod(true);
            DestroyCategories = new[] {
                ResolveDestroyCategory("VRC.Dynamics.VRCPhysBoneBase"),
                ResolveDestroyCategory("VRC.Dynamics.VRCPhysBoneColliderBase"),
                ResolveDestroyCategory("VRC.Dynamics.ContactBase")
            };
        }

        private static DestroyCategoryMembers ResolveDestroyCategory(string typeName) {
            var componentType = ReflectionUtils.FindType(typeName);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) return null;
            var getRootTransform = ReflectionUtils.FindMethodWithSignature(
                componentType, "GetRootTransform", typeof(Transform));
            if (getRootTransform == null) return null;
            return new DestroyCategoryMembers {
                ComponentType = componentType,
                GetRootTransform = getRootTransform
            };
        }

        internal static void DemandArmatureLink() {
            EnsureResolved();
            ReflectionUtils.Demand(ArmatureLinkApply, "ArmatureLinkService.Apply()");
            ReflectionUtils.Demand(ArmatureLinkAvatarField,
                "ArmatureLinkService.avatarObject");
            VfGameObjectCompat.DemandCore();
        }

        internal static void DemandHapticSockets() {
            EnsureResolved();
            ReflectionUtils.Demand(HapticSocketsApply, "BakeHapticSocketsService.Apply()");
            ReflectionUtils.Demand(HapticSocketsAvatarField,
                "BakeHapticSocketsService.avatarObject");
            VfGameObjectCompat.DemandCore();
        }

        internal static GameObject GetGameObject(object vfGameObject) {
            return VfGameObjectCompat.Unwrap(vfGameObject);
        }

        internal static GameObject GetAvatar(object serviceInstance, FieldInfo avatarField) {
            if (serviceInstance == null || avatarField == null) return null;
            return GetGameObject(avatarField.GetValue(serviceInstance));
        }
    }
}
