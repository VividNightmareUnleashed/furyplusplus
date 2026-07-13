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

            var ok = true;
            var strip = StripUnusedParamsModule.Instance;
            if (strip != null && ModuleRegistry.IsActive(strip) && strip.Enabled) {
                ok &= StripUnusedParamsPass.Run(descriptor);
            }
            // Int→Bool narrowing joins here later, after the strip pass has soaked.
            return ok;
        }
    }
}
