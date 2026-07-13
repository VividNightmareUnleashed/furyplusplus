using System;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * VRCFury creates a separate asset file for every generated material, mesh,
     * texture, menu and parameter root. Unity spends far more time importing those
     * files than attaching subassets. Keep controllers as main .controller assets and
     * consolidate all other roots into one generated container per SaveAssets pass.
     */
    internal sealed class ConsolidatedAssetContainerModule : Module {
        internal static ConsolidatedAssetContainerModule Instance { get; private set; }

        internal ConsolidatedAssetContainerModule() {
            Instance = this;
        }

        internal override string Id => "consolidatedAssetContainer";
        internal override string DisplayName => "Consolidated asset container";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Attaches generated non-controller assets to one container file instead of importing a separate file per root.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ConsolidatedAssetContainerPatch.Install(harmony, compat);
        }
    }

    internal static class ConsolidatedAssetContainerPatch {
        private sealed class Context {
            internal Object Container;
            internal bool CreatingContainer;
        }

        [ThreadStatic] private static Context active;
        private static Type containerType;
        private static MethodInfo saveAsset;
        private static MethodInfo attachAsset;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            SaveAssetsCompat.EnsureResolved();
            var run = SaveAssetsCompat.SaveAssetsRun;
            var databaseType = ReflectionUtils.FindType("VF.Utils.VRCFuryAssetDatabase");
            containerType = ReflectionUtils.FindType(
                "VF.Utils.VRCFuryAssetDatabase+BinaryContainer"
            );
            saveAsset = ReflectionUtils.FindMethodWithSignature(
                databaseType,
                "SaveAsset",
                typeof(void),
                typeof(Object),
                typeof(string),
                typeof(string)
            );
            attachAsset = ReflectionUtils.FindMethodWithSignature(
                databaseType,
                "AttachAsset",
                typeof(void),
                typeof(Object),
                typeof(Object)
            );

            if (run == null || containerType == null || saveAsset == null || attachAsset == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                run,
                prefix: new HarmonyMethod(typeof(ConsolidatedAssetContainerPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ConsolidatedAssetContainerPatch), nameof(End))
            );
            harmony.Patch(
                saveAsset,
                prefix: new HarmonyMethod(typeof(ConsolidatedAssetContainerPatch), nameof(Save))
            );
        }

        private static void Begin() {
            active = ConsolidatedAssetContainerModule.Instance?.Enabled == true ? new Context() : null;
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        private static bool Save(Object obj, string dir) {
            var context = active;
            if (context == null || context.CreatingContainer
                                || obj == null || obj is AnimatorController) return true;

            try {
                if (context.Container == null) {
                    context.Container = ScriptableObject.CreateInstance(containerType);
                    context.Container.name = "VRCFury Generated Assets";
                    context.CreatingContainer = true;
                    try {
                        ReflectionUtils.InvokeUnwrapped(
                            saveAsset,
                            null,
                            new object[] { context.Container, dir, "VRCFury Generated Assets" }
                        );
                    } finally {
                        context.CreatingContainer = false;
                    }
                }

                ReflectionUtils.InvokeUnwrapped(attachAsset, null, new[] { obj, context.Container });
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn(
                    "Consolidated asset container fell back to separate files: " + e.Message
                );
                return true;
            }
        }
    }
}
