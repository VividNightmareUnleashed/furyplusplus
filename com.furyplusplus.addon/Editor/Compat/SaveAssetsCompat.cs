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
        internal static MethodInfo SaveAssetsRunTransaction { get; private set; }
        internal static MethodInfo FactoryDidCreate { get; private set; }
        internal static MethodInfo SaveAsset2 { get; private set; }
        internal static MethodInfo SaveAsset3 { get; private set; }
        internal static MethodInfo AttachAsset { get; private set; }
        internal static MethodInfo Finish { get; private set; }
        internal static MethodInfo FactoryPrune { get; private set; }

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
            SaveAssetsRunTransaction = ReflectionUtils.FindUniqueMethod(
                saveAssetsType, "Run", method => method.ReturnType == typeof(void)
                                                 && method.GetParameters().Length == 1);

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

            var assetDbType = ReflectionUtils.FindType("VF.Utils.VRCFuryAssetDatabase");
            SaveAsset2 = ReflectionUtils.FindUniqueMethod(
                assetDbType, "SaveAsset", method => method.GetParameters().Length == 2);
            SaveAsset3 = ReflectionUtils.FindUniqueMethod(
                assetDbType, "SaveAsset", method => method.GetParameters().Length == 3);
            AttachAsset = ReflectionUtils.FindUniqueMethod(
                assetDbType, "AttachAsset", method => method.GetParameters().Length == 2);

            var sessionType = ReflectionUtils.FindType("VF.Utils.SaveAssetsSession");
            Finish = ReflectionUtils.FindNoArgVoid(sessionType, "Finish");
            FactoryPrune = ReflectionUtils.FindNoArgVoid(factoryType, "Prune");
        }

        internal static void DemandNoDiskSave() {
            EnsureResolved();
            ReflectionUtils.Demand(
                SaveAssetsRunTransaction,
                "SaveAssetsService.Run(IEnumerable<ControllerManager>)");
            ReflectionUtils.Demand(SaveAsset2, "VRCFuryAssetDatabase.SaveAsset(obj, fullPath)");
            ReflectionUtils.Demand(SaveAsset3, "VRCFuryAssetDatabase.SaveAsset(obj, dir, filename)");
            ReflectionUtils.Demand(AttachAsset, "VRCFuryAssetDatabase.AttachAsset(obj, parent)");
            ReflectionUtils.Demand(Finish, "SaveAssetsSession.Finish()");
            ReflectionUtils.Demand(FactoryPrune, "VrcfObjectFactory.Prune()");
        }
    }
}
