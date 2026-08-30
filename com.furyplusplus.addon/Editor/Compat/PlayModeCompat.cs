using System.Reflection;

namespace FuryPlusPlus {
    /** VRCFury upload-only service entry points that can be skipped in play mode. */
    internal static class PlayModeCompat {
        private static bool resolved;

        internal static MethodInfo MipmapApply { get; private set; }
        internal static MethodInfo MenuIconApply { get; private set; }
        internal static MethodInfo ValidationApply { get; private set; }

        internal static void DemandCore() {
            if (!resolved) {
                resolved = true;
                MipmapApply = ResolveApply("VF.Service.FixMipmapStreamingService");
                MenuIconApply = ResolveApply("VF.Service.FixMenuIconTexturesService");
                ValidationApply = ResolveApply("VF.Service.FinalValidationService");
            }
            ReflectionUtils.Demand(MipmapApply, "FixMipmapStreamingService.Apply()");
            ReflectionUtils.Demand(MenuIconApply, "FixMenuIconTexturesService.Apply()");
            ReflectionUtils.Demand(ValidationApply, "FinalValidationService.Apply()");
        }

        private static MethodInfo ResolveApply(string typeName) {
            return ReflectionUtils.FindNoArgVoid(ReflectionUtils.FindType(typeName), "Apply");
        }
    }
}
