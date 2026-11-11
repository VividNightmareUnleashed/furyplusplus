using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /** Reflected haptics surfaces shared by SPS speed patches. */
    internal static class SpsCompat {
        private static bool resolved;

        internal static MethodInfo HasDpsOrTpsMaterial { get; private set; }

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            // VRCFury 1.1443.0 removed TpsConfigurer (TPS autoconfiguration) and moved the
            // probe, unchanged, to TpsUtils. Both run the same DpsUtils.IsDps || IsTps test.
            HasDpsOrTpsMaterial = ReflectionUtils.FindMethodWithSignature(
                                      ReflectionUtils.FindType("VF.Builder.Haptics.TpsUtils"),
                                      "HasDpsOrTpsMaterial", typeof(bool), typeof(Renderer))
                                  ?? ReflectionUtils.FindMethodWithSignature(
                                      ReflectionUtils.FindType("VF.Builder.Haptics.TpsConfigurer"),
                                      "HasDpsOrTpsMaterial", typeof(bool), typeof(Renderer));
        }

        internal static void DemandMaterialProbe() {
            EnsureResolved();
            ReflectionUtils.Demand(HasDpsOrTpsMaterial,
                "TpsUtils.HasDpsOrTpsMaterial(Renderer)");
        }
    }
}
