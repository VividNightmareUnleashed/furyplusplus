using System;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Fail-soft holder for NDMF's processed-avatar marking, used by the bake-cache replay:
     * a replayed avatar must look to NDMF exactly like one a completed build chain produced,
     * or NDMF's play-mode activator fires a redundant second OnPreprocessAvatar at it. Two
     * markers make that true (mirroring what a real NDMF build sets):
     *
     *  - AlreadyProcessedTag { processingCompleted = true } on the avatar root — honored by
     *    BuildFrameworkPreprocessHook, BuildFrameworkOptimizeHook, and AvatarProcessor.
     *    VRCFury itself creates this tag via reflection, so the shape is a stable contract.
     *  - a HookDedup weak-table entry with both phase flags set — ApplyOnPlay.MaybeProcessAvatar
     *    checks HookDedup.HasAvatar BEFORE calling OnPreprocessAvatar at all.
     *
     * Everything is fail-soft: NDMF absent (or reshaped) degrades to returning false, which
     * is safe — without NDMF there is no NDMF initiator to suppress, and any stray repeat
     * call still hits VRCFury's own RunPreprocessorsOnlyOncePatch exactly as stock.
     */
    internal static class NdmfCompat {
        private static bool resolved;
        private static bool warned;

        private static Type tagType;
        private static Type activatorType;
        private static FieldInfo processingCompleted;
        private static MethodInfo recordAvatar;
        private static FieldInfo ranEarlyHook;
        private static FieldInfo ranOptimization;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            tagType = ReflectionUtils.FindType("nadena.dev.ndmf.runtime.AlreadyProcessedTag");
            activatorType = ReflectionUtils.FindType("nadena.dev.ndmf.runtime.AvatarActivator");
            processingCompleted = tagType?.GetField("processingCompleted",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var hookDedup = ReflectionUtils.FindType("nadena.dev.ndmf.HookDedup");
            recordAvatar = hookDedup?.GetMethod("RecordAvatar",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(GameObject) }, null);
            var stateType = recordAvatar?.ReturnType;
            const BindingFlags instanceAny =
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            ranEarlyHook = stateType?.GetField("ranEarlyHook", instanceAny);
            ranOptimization = stateType?.GetField("ranOptimization", instanceAny);
        }

        /**
         * NDMF's per-avatar play-mode activation component. It self-destructs in its own
         * Update, and NDMF's global activator may be INSIDE GetOrAddComponent for it when a
         * replay fires (AddComponent → Awake → OnPreprocessAvatar → replay), so destroying it
         * mid-restore null-refs NDMF — the restore must leave it alone.
         */
        internal static bool IsNdmfActivator(Component component) {
            EnsureResolved();
            return activatorType != null && activatorType.IsInstanceOfType(component);
        }

        /** The avatar carries a completed AlreadyProcessedTag. False when NDMF is absent. */
        internal static bool IsMarkedProcessed(GameObject avatar) {
            EnsureResolved();
            if (avatar == null || tagType == null || processingCompleted == null) return false;
            try {
                var tag = avatar.GetComponent(tagType);
                return tag != null && processingCompleted.GetValue(tag) is bool done && done;
            } catch {
                return false;
            }
        }

        /**
         * Mark a replayed avatar as fully NDMF-processed (tag + dedup table). Returns false —
         * after one warning — when NDMF is absent or its internals no longer match.
         */
        internal static bool TryMarkProcessed(GameObject avatar) {
            EnsureResolved();
            if (avatar == null || tagType == null || processingCompleted == null) return false;
            try {
                var tag = avatar.GetComponent(tagType);
                if (tag == null) tag = avatar.AddComponent(tagType);
                tag.hideFlags = HideFlags.HideAndDontSave;
                processingCompleted.SetValue(tag, true);
                if (recordAvatar != null) {
                    var state = recordAvatar.Invoke(null, new object[] { avatar });
                    if (state != null) {
                        ranEarlyHook?.SetValue(state, true);
                        ranOptimization?.SetValue(state, true);
                    }
                }
                return true;
            } catch (Exception e) {
                if (!warned) {
                    warned = true;
                    Log.Warn("NDMF processed-marking unavailable; repeat preprocess calls fall "
                             + "back to VRCFury's own dedup: " + e.Message);
                }
                return false;
            }
        }
    }
}
