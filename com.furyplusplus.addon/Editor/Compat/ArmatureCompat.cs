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
