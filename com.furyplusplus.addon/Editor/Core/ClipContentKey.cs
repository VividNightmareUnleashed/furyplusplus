using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * The clip-content serialization ClipDedupPass keys on: two clips count as identical
     * only when everything appended here matches. Callers keep their own curve sources and
     * preambles and append entries through here; coverage is deliberately broad (settings,
     * frame rate, wrap modes, bounds, events, every keyframe facet), so a facet added here
     * tightens every consumer at once rather than letting two of them disagree.
     */
    internal static class ClipContentKey {
        /** Clip-level facts (settings, frame rate, wrap mode, bounds, events). */
        internal static void AppendClipFacts(StringBuilder builder, AnimationClip clip) {
            builder.Append("clip|")
                .Append(Float(clip.frameRate)).Append('|')
                .Append(clip.legacy).Append('|')
                .Append(clip.wrapMode).Append('|');
            AppendBounds(builder, clip.localBounds);

            foreach (var (name, value) in AnimationClipCompat.SettingsOf(clip)) {
                builder.Append("setting|").Append(name).Append('|')
                    .Append(Value(value)).AppendLine();
            }

            foreach (var animationEvent in AnimationUtility.GetAnimationEvents(clip)) {
                builder.Append("event|").Append(Float(animationEvent.time)).Append('|')
                    .Append(animationEvent.functionName).Append('|')
                    .Append(Float(animationEvent.floatParameter)).Append('|')
                    .Append(animationEvent.intParameter).Append('|')
                    .Append(animationEvent.stringParameter).Append('|')
                    .Append(ObjectId(animationEvent.objectReferenceParameter)).Append('|')
                    .Append(animationEvent.messageOptions).AppendLine();
            }
        }

        /**
         * One (binding, FloatOrObjectCurve) entry. False = the entry cannot be hashed
         * faithfully (null curve/keys) — the caller must treat the whole clip as
         * un-dedupable rather than guess.
         */
        internal static bool TryAppendCurve(StringBuilder builder, EditorCurveBinding binding, object curve) {
            if (curve == null) return false;
            var isFloat = ClipCurveCompat.IsFloat(curve);
            builder.Append(isFloat ? "float|" : "object|")
                .Append(binding.path).Append('|')
                .Append(binding.type?.AssemblyQualifiedName).Append('|')
                .Append(binding.propertyName).Append('|')
                .Append(binding.isPPtrCurve).Append('|')
                .Append(binding.isDiscreteCurve).AppendLine();

            if (isFloat) {
                var animationCurve = ClipCurveCompat.FloatCurveOf(curve);
                if (animationCurve == null) return false;
                builder.Append("wrap|").Append(animationCurve.preWrapMode).Append('|')
                    .Append(animationCurve.postWrapMode).AppendLine();
                foreach (var key in animationCurve.keys) {
                    builder.Append("key|").Append(Float(key.time)).Append('|')
                        .Append(Float(key.value)).Append('|')
                        .Append(Float(key.inTangent)).Append('|')
                        .Append(Float(key.outTangent)).Append('|')
                        .Append(Float(key.inWeight)).Append('|')
                        .Append(Float(key.outWeight)).Append('|')
                        .Append(key.weightedMode).AppendLine();
                }
            } else {
                var objectCurve = ClipCurveCompat.ObjectCurveOf(curve);
                if (objectCurve == null) return false;
                foreach (var key in objectCurve) {
                    builder.Append("key|").Append(Float(key.time)).Append('|')
                        .Append(ObjectId(key.value)).AppendLine();
                }
            }
            return true;
        }

        /** Deterministic entry order shared by every key builder. */
        internal static void SortByBinding<T>(List<T> entries, Func<T, EditorCurveBinding> bindingOf) {
            entries.Sort((a, b) => Compare(bindingOf(a), bindingOf(b)));
        }

        private static int Compare(EditorCurveBinding a, EditorCurveBinding b) {
            var result = string.CompareOrdinal(a.path, b.path);
            if (result != 0) return result;
            result = string.CompareOrdinal(a.type?.AssemblyQualifiedName, b.type?.AssemblyQualifiedName);
            if (result != 0) return result;
            result = string.CompareOrdinal(a.propertyName, b.propertyName);
            if (result != 0) return result;
            result = a.isPPtrCurve.CompareTo(b.isPPtrCurve);
            if (result != 0) return result;
            return a.isDiscreteCurve.CompareTo(b.isDiscreteCurve);
        }

        private static string Float(float value) {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Value(object value) {
            if (value == null) return "<null>";
            if (value is Object unityObject) return ObjectId(unityObject);
            if (value is IFormattable formatted) {
                return formatted.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        /** Stable for persisted assets (guid:localId), instance id otherwise. */
        private static string ObjectId(Object value) {
            if (value == null) return "<null>";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId)) {
                return guid + ":" + localId;
            }
            return "instance:" + value.GetInstanceID();
        }

        private static void AppendBounds(StringBuilder builder, Bounds bounds) {
            builder.Append(Float(bounds.center.x)).Append(',')
                .Append(Float(bounds.center.y)).Append(',')
                .Append(Float(bounds.center.z)).Append('|')
                .Append(Float(bounds.extents.x)).Append(',')
                .Append(Float(bounds.extents.y)).Append(',')
                .Append(Float(bounds.extents.z)).AppendLine();
        }
    }
}
