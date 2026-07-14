using System.Reflection;

namespace FuryPlusPlus {
    /**
     * Lazy holder for VRCFury's IsActuallyUploadingHook.Get() — the "is this SDK build a
     * real upload" gate every play-mode-only module checks. Resolved once; consumers call
     * DemandCore() from Install() (fail-closed) and pass their own failure direction:
     * upload-only skips assume TRUE on failure (never skip a maybe-upload), play-mode
     * caches assume TRUE too (never cache a maybe-upload).
     */
    internal static class UploadCompat {
        private static bool resolved;
        private static MethodInfo isActuallyUploading; // VF.Hooks.IsActuallyUploadingHook.Get()

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;
            isActuallyUploading = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VF.Hooks.IsActuallyUploadingHook"),
                "Get",
                typeof(bool)
            );
        }

        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(isActuallyUploading, "IsActuallyUploadingHook.Get()");
        }

        /** The resolved hook itself, for modules that PATCH it rather than call it. */
        internal static MethodInfo HookMethod => isActuallyUploading;

        internal static bool IsActuallyUploading(bool assumeOnFailure) {
            try {
                return (bool)isActuallyUploading.Invoke(null, null);
            } catch {
                return assumeOnFailure;
            }
        }
    }
}
