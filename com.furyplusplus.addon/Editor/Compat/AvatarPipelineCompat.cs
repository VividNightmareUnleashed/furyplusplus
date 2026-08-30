using System;
using System.Reflection;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus {
    /** Reflected avatar-component surfaces used by standalone preprocessing passes. */
    internal static class AvatarPipelineCompat {
        private static bool resolved;

        private static Type vrcfuryComponentType;
        private static FieldInfo vrcfuryContent;
        private static Type directTreeOptimizerType;
        private static Type vrcfuryTestType;
        private static Type physBoneType;
        private static FieldInfo physBoneParameter;
        private static Type contactType;
        private static PropertyInfo contactParameterProperty;
        private static FieldInfo contactParameterField;
        private static Type pipelineManagerType;
        private static FieldInfo blueprintIdField;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;
            const BindingFlags instance =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            vrcfuryComponentType = ReflectionUtils.FindType("VF.Model.VRCFury");
            vrcfuryContent = vrcfuryComponentType?.GetField("content", instance);
            directTreeOptimizerType = ReflectionUtils.FindType(
                "VF.Model.Feature.DirectTreeOptimizer");
            vrcfuryTestType = ReflectionUtils.FindType("VF.Model.VRCFuryTest");

            physBoneType = ReflectionUtils.FindType(
                "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            physBoneParameter = physBoneType?.GetField("parameter", instance);
            contactType = ReflectionUtils.FindType(
                "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver");
            contactParameterProperty = contactType?.GetProperty("parameter", instance);
            contactParameterField = contactType?.GetField("parameter", instance);

        }

        internal static void DemandFullScopeDbt() {
            EnsureResolved();
            ReflectionUtils.Demand(vrcfuryComponentType, "VF.Model.VRCFury");
            ReflectionUtils.Demand(vrcfuryContent, "VRCFury.content");
            ReflectionUtils.Demand(directTreeOptimizerType,
                "VF.Model.Feature.DirectTreeOptimizer");
        }

        internal static void DemandParameterPasses() {
            EnsureResolved();
            ReflectionUtils.Demand(vrcfuryTestType, "VF.Model.VRCFuryTest");
        }

        internal static void DemandVrcfuryTestMarker() {
            EnsureResolved();
            ReflectionUtils.Demand(vrcfuryTestType, "VF.Model.VRCFuryTest");
        }

        internal static bool VrcfuryRanOn(GameObject avatarObject) {
            EnsureResolved();
            return vrcfuryTestType != null && avatarObject.GetComponent(vrcfuryTestType) != null;
        }

        internal static string GetBlueprintId(VRCAvatarDescriptor descriptor) {
            EnsureResolved();
            if (descriptor == null) return null;
            foreach (var component in descriptor.GetComponents<Component>()) {
                if (component == null) continue;
                var type = component.GetType();
                if (type.Name != "PipelineManager") continue;
                if (pipelineManagerType != type) {
                    pipelineManagerType = type;
                    blueprintIdField = type.GetField(
                        "blueprintId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                return blueprintIdField?.GetValue(component) as string;
            }
            return null;
        }

        internal static bool TryGetDynamicsParameter(Component component, out string parameter,
            out bool isPhysBone) {
            EnsureResolved();
            parameter = null;
            isPhysBone = false;
            if (component == null) return false;
            var type = component.GetType();
            if (physBoneType != null && physBoneType.IsAssignableFrom(type)) {
                parameter = physBoneParameter?.GetValue(component) as string;
                isPhysBone = true;
                return !string.IsNullOrEmpty(parameter);
            }
            if (contactType == null || !contactType.IsAssignableFrom(type)) return false;
            parameter = contactParameterProperty?.GetValue(component) as string
                        ?? contactParameterField?.GetValue(component) as string;
            return !string.IsNullOrEmpty(parameter);
        }

        internal static bool HasDirectTreeOptimizer(GameObject avatarObject) {
            DemandFullScopeDbt();
            foreach (var component in avatarObject.GetComponentsInChildren(vrcfuryComponentType, true)) {
                var content = vrcfuryContent.GetValue(component);
                if (content != null && directTreeOptimizerType.IsInstanceOfType(content)) return true;
            }
            return false;
        }

        internal static bool HasVrcfuryComponent(GameObject avatarObject) {
            DemandFullScopeDbt();
            return avatarObject.GetComponentsInChildren(vrcfuryComponentType, true).Length > 0;
        }

        internal static void AddDirectTreeOptimizer(GameObject avatarObject) {
            DemandFullScopeDbt();
            var added = avatarObject.AddComponent(vrcfuryComponentType);
            vrcfuryContent.SetValue(added, Activator.CreateInstance(directTreeOptimizerType));
        }
    }
}
