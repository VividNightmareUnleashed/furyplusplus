using System;
using System.Collections.Generic;
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
        private static FieldInfo contactAdvancedParameters;
        private static FieldInfo contactCollisionTags;
        private static readonly Dictionary<string, long> AdvancedMasks =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static Type raycastType;
        private static PropertyInfo raycastParameter;
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
            // Both new in VRC SDK 3.10.6; collisionTags is inherited from ContactBase.
            contactAdvancedParameters = contactType?.GetField("advancedParameters", instance);
            contactCollisionTags = contactType?.GetField("collisionTags", instance);
            var flagsType = contactAdvancedParameters?.FieldType;
            if (flagsType != null && flagsType.IsEnum) {
                foreach (var name in Enum.GetNames(flagsType)) {
                    AdvancedMasks[name] = Convert.ToInt64(Enum.Parse(flagsType, name));
                }
            } else if (flagsType == typeof(bool)) {
                foreach (var group in AdvancedContactGroups) AdvancedMasks[group.Flag] = -1L;
                AdvancedMasks["CollisionTags"] = -1L;
            }

            raycastType = ReflectionUtils.FindType("VRC.SDK3.Avatars.Components.VRCRaycast");
            raycastParameter = raycastType?.GetProperty("Parameter", instance);
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

        private static readonly string[] PhysBoneSuffixes =
            { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        private static readonly string[] RaycastSuffixes = { "_Hit", "_Ratio", "_Distance" };

        /**
         * ContactReceiver.AdvancedParameter groups (VRC SDK 3.10.6) and the suffixes each one
         * binds, from AvatarDynamicsSetup.Contact_OnInitialize. ProximitySpeed is only bound
         * on Proximity receivers; it is listed whenever its flag is set, which only makes the
         * parameter passes more conservative.
         */
        private static readonly (string Flag, string[] Suffixes)[] AdvancedContactGroups = {
            ("SenderType", new[] { "_SenderType" }),
            ("SenderLocalPosition", new[] {
                "_SenderLocalPositionX", "_SenderLocalPositionY", "_SenderLocalPositionZ"
            }),
            ("SenderLossyScale", new[] {
                "_SenderLossyScaleX", "_SenderLossyScaleY", "_SenderLossyScaleZ"
            }),
            ("SenderVelocity", new[] {
                "_SenderVelocityX", "_SenderVelocityY", "_SenderVelocityZ", "_SenderVelocityMagnitude"
            }),
            ("PointSeparation", new[] { "_PointSeparation" }),
            ("ProximitySpeed", new[] { "_ProximitySpeed" }),
        };

        private const string CollisionTagPrefix = "_CollisionTag_";

        /**
         * Every animator parameter a PhysBone, contact receiver or VRC Raycast writes at
         * runtime; empty for any other component. Contact receivers include the advanced
         * parameters their flags select and one bool per non-blank collision tag. On an SDK
         * without advanced contact parameters or VRCRaycast those parts are simply absent.
         */
        internal static List<string> GetDynamicsParameterNames(Component component) {
            EnsureResolved();
            var names = new List<string>();
            if (component == null) return names;
            var type = component.GetType();

            if (physBoneType != null && physBoneType.IsAssignableFrom(type)) {
                var parameter = physBoneParameter?.GetValue(component) as string;
                if (string.IsNullOrEmpty(parameter)) return names;
                foreach (var suffix in PhysBoneSuffixes) names.Add(parameter + suffix);
                return names;
            }

            if (raycastType != null && raycastType.IsAssignableFrom(type)) {
                var parameter = raycastParameter?.GetValue(component) as string;
                if (string.IsNullOrEmpty(parameter)) return names;
                foreach (var suffix in RaycastSuffixes) names.Add(parameter + suffix);
                return names;
            }

            if (contactType == null || !contactType.IsAssignableFrom(type)) return names;
            var receiverParameter = contactParameterProperty?.GetValue(component) as string
                                    ?? contactParameterField?.GetValue(component) as string;
            if (string.IsNullOrEmpty(receiverParameter)) return names;
            names.Add(receiverParameter);

            if (contactAdvancedParameters == null) return names;
            var flags = AdvancedFlagsOf(component);
            if (flags == 0) return names;
            foreach (var group in AdvancedContactGroups) {
                if (!AdvancedMasks.TryGetValue(group.Flag, out var mask) || (flags & mask) == 0) continue;
                foreach (var suffix in group.Suffixes) names.Add(receiverParameter + suffix);
            }
            if (AdvancedMasks.TryGetValue("CollisionTags", out var tagMask) && (flags & tagMask) != 0
                && contactCollisionTags?.GetValue(component) is IEnumerable<string> tags) {
                foreach (var tag in tags) {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    names.Add(receiverParameter + CollisionTagPrefix + tag);
                }
            }
            return names;
        }

        private static long AdvancedFlagsOf(Component receiver) {
            var value = contactAdvancedParameters.GetValue(receiver);
            // 3.10.6-beta.3 shipped advancedParameters as a bool meaning every group.
            if (value is bool enabled) return enabled ? -1L : 0L;
            return value == null ? 0L : Convert.ToInt64(value);
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
