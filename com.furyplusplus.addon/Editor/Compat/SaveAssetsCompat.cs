using System;
using System.Reflection;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * Lazy compat holder for the SaveAssets-family modules: SaveAssetsService.Run and
     * VrcfObjectFactory.DidCreate. Resolved once on first module Install; callers treat
     * null members as an install failure (fail-closed).
     */
    internal static class SaveAssetsCompat {
        private static bool resolved;

        internal static MethodInfo SaveAssetsRun { get; private set; }
        internal static MethodInfo FactoryDidCreate { get; private set; }

        // DidCreate runs once per visited node in the controller-graph traversal, so a
        // bound delegate replaces the MethodInfo.Invoke + object[] allocation there.
        private static Func<Object, bool> didCreateFast;

        internal static bool DidCreate(Object asset) {
            if (didCreateFast != null) return didCreateFast(asset);
            return (bool)ReflectionUtils.InvokeUnwrapped(FactoryDidCreate, null, new object[] { asset });
        }

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            // PORT-NOTE: QuickFury resolved SaveAssetsService from the VRCFury-Editor-Avatars
            // assembly reference it already held; FindType's AppDomain scan resolves the same
            // unique type.
            var saveAssetsType = ReflectionUtils.FindType("VF.Service.SaveAssetsService");
            SaveAssetsRun = ReflectionUtils.FindNoArgVoid(saveAssetsType, "Run");

            var factoryType = ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory");
            FactoryDidCreate = ReflectionUtils.FindUniqueMethod(
                factoryType,
                "DidCreate",
                method => method.ReturnType == typeof(bool) && method.GetParameters().Length == 1
            );
            if (FactoryDidCreate != null) {
                try {
                    didCreateFast = (Func<Object, bool>)Delegate.CreateDelegate(
                        typeof(Func<Object, bool>),
                        FactoryDidCreate
                    );
                } catch (ArgumentException) {
                    // Parameter type drifted from Object; the reflection fallback still works.
                }
            }
        }
    }
}
