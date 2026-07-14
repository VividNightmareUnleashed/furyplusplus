using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus {
    /**
     * FuryPlusPlus's post-build seam: runs on the fully-built avatar AFTER VRCFury's main
     * build (VrcPreuploadHook, order −10000) and BEFORE its parameter compressor
     * (ParameterCompressorHook, int.MaxValue−100) — expression parameters are complete but
     * not yet compressed, so bits reclaimed here shrink the compressor's work. Hosts the
     * whole-avatar quality passes in a fixed order; each is gated by its own module.
     */
    internal class FppPostBuildHook : GuardedPreprocessorPass {
        public override int callbackOrder => 1_000_000;

        // The SDK preprocessor chain may visit a root more than once across triggers; the
        // whole chain runs within one editor tick, so a delayCall-cleared latch bounds us
        // to one pass per root per build (mirrors VRCFury's own once-per-object guard).
        private static readonly HashSet<int> ProcessedThisTick = new HashSet<int>();
        private static bool clearScheduled;

        protected override bool Run(GameObject avatarObject) {
            var descriptor = avatarObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return true;
            if (!StripUnusedParamsPass.VrcfuryRanOn(avatarObject)) return true;

            if (!ProcessedThisTick.Add(avatarObject.GetInstanceID())) return true;
            if (!clearScheduled) {
                clearScheduled = true;
                EditorApplication.delayCall += () => {
                    ProcessedThisTick.Clear();
                    clearScheduled = false;
                };
            }

            // Never touch a user asset: post-build the descriptor must reference VRCFury's
            // generated params copy. Anything else is an unexpected pipeline state.
            var paramsAsset = descriptor.expressionParameters;
            var assetPath = paramsAsset == null ? null : AssetDatabase.GetAssetPath(paramsAsset);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Packages/com.vrcfury.temp")) {
                Log.Warn("Post-build parameter passes skipped: expression parameters are not a " +
                         "VRCFury-generated asset (" +
                         (string.IsNullOrEmpty(assetPath) ? "unsaved" : assetPath) + ").");
                return true;
            }

            var index = ParamUsageIndex.Build(descriptor);

            var stripped = ModuleRegistry.IsOn(StripUnusedParamsModule.Instance)
                ? StripUnusedParamsPass.Run(descriptor, index)
                : new List<string>();

            var narrowed = ModuleRegistry.IsOn(NarrowIntParamsModule.Instance)
                ? NarrowIntParamsPass.Run(descriptor, index)
                : new List<string>();

            return StripUnusedParamsPass.HandleCrossPlatform(descriptor, stripped, narrowed);
        }
    }
}
