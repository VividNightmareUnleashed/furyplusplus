using System;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /** Reflected haptics surfaces shared by SPS speed patches. */
    internal static class SpsCompat {
        private static bool resolved;

        internal static Type PlugComponentType { get; private set; }
        internal static MethodInfo UpgraderApply { get; private set; }
        internal static MethodInfo GetRenderers { get; private set; }
        internal static MethodInfo GetAutoWorldSize { get; private set; }
        internal static MethodInfo HasDpsOrTpsMaterial { get; private set; }

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            PlugComponentType = ReflectionUtils.FindType("VF.Component.VRCFuryHapticPlug");
            UpgraderApply = ReflectionUtils.FindUniqueMethod(
                ReflectionUtils.FindType("VF.Builder.Haptics.SpsUpgrader"), "Apply",
                method => method.GetParameters().Length == 3);
            GetRenderers = ReflectionUtils.FindUniqueMethod(
                ReflectionUtils.FindType("VF.Inspector.VRCFuryHapticPlugEditor"), "GetRenderers",
                method => method.GetParameters().Length == 1);
            GetAutoWorldSize = ReflectionUtils.FindUniqueMethod(
                ReflectionUtils.FindType("VF.Builder.Haptics.PlugSizeDetector"), "GetAutoWorldSize",
                method => method.GetParameters().Length == 1
                          && method.GetParameters()[0].ParameterType == typeof(Renderer));
            HasDpsOrTpsMaterial = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VF.Builder.Haptics.TpsConfigurer"),
                "HasDpsOrTpsMaterial", typeof(bool), typeof(Renderer));
        }

        internal static void DemandCoveredRenderer() {
            EnsureResolved();
            ReflectionUtils.Demand(PlugComponentType, "VF.Component.VRCFuryHapticPlug");
            ReflectionUtils.Demand(UpgraderApply, "SpsUpgrader.Apply(...)");
            ReflectionUtils.Demand(GetRenderers, "VRCFuryHapticPlugEditor.GetRenderers(...)");
            ReflectionUtils.Demand(GetAutoWorldSize, "PlugSizeDetector.GetAutoWorldSize(Renderer)");
        }

        internal static void DemandMaterialProbe() {
            EnsureResolved();
            ReflectionUtils.Demand(HasDpsOrTpsMaterial,
                "TpsConfigurer.HasDpsOrTpsMaterial(Renderer)");
        }
    }
}
