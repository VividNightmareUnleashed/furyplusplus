using System;
using System.Collections.Generic;
using UnityEditor;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * Serialized-reference graph utilities for the bake-cache snapshot: discover the transient
     * dependency closure of an object graph (Walk), deep-copy it (CloneAll), and rewrite
     * references from originals to copies (Remap). Object.Instantiate copies one object's
     * serialized data but leaves its references shared — that is exactly why Walk discovers the
     * closure transitively and Remap rewires every copy afterwards. Deliberately free of
     * VRCFury/NDMF/module types so it is unit-testable with synthetic objects.
     */
    internal static class ObjectGraphCloner {
        internal enum RefKind {
            /** Keep the reference as-is: persisted asset, scene object inside the prefab, etc. */
            AsIs,
            /** Transient: joins the closure, gets cloned, and is traversed transitively. */
            Clone,
            /** Disqualifying (e.g. external scene object): surfaced so the caller can refuse. */
            Reject
        }

        internal sealed class WalkResult {
            /** Clone-classified objects in stable discovery order. */
            internal readonly List<Object> ToClone = new List<Object>();
            internal readonly List<Object> Rejected = new List<Object>();
            internal bool HasRejections => Rejected.Count > 0;
        }

        /**
         * BFS over serialized object references starting from roots. Each referenced object is
         * classified once; traversal continues only THROUGH Clone-classified objects (AsIs
         * objects are opaque leaves), so persisted assets never drag their own dependency
         * trees in. Cycle-safe. The roots themselves are traversed, never classified.
         */
        internal static WalkResult Walk(IEnumerable<Object> roots, Func<Object, RefKind> classify) {
            var result = new WalkResult();
            var seen = new HashSet<Object>();
            var queue = new Queue<Object>();
            foreach (var root in roots) {
                if (root != null) queue.Enqueue(root);
            }
            while (queue.Count > 0) {
                foreach (var reference in CollectObjectReferences(queue.Dequeue())) {
                    if (!seen.Add(reference)) continue;
                    switch (classify(reference)) {
                        case RefKind.Clone:
                            result.ToClone.Add(reference);
                            queue.Enqueue(reference);
                            break;
                        case RefKind.Reject:
                            result.Rejected.Add(reference);
                            break;
                    }
                }
            }
            return result;
        }

        /**
         * Object.Instantiate every entry, preserving names (no "(Clone)" suffixes — snapshot
         * asset names must be stable across captures). Callers should not pass GameObjects:
         * instantiating one clones its whole hierarchy, which is never what the closure means.
         */
        internal static Dictionary<Object, Object> CloneAll(IReadOnlyList<Object> toClone) {
            var map = new Dictionary<Object, Object>();
            foreach (var original in toClone) {
                var copy = Object.Instantiate(original);
                copy.name = original.name;
                map[original] = copy;
            }
            return map;
        }

        /**
         * Rewrite every serialized object reference on every target through the map. Targets
         * must include the copies themselves — their references still point at the originals
         * until this runs. m_Script is never rewritten.
         */
        internal static void Remap(IEnumerable<Object> targets, IReadOnlyDictionary<Object, Object> map) {
            foreach (var target in targets) {
                if (target == null) continue;
                using (var serialized = new SerializedObject(target)) {
                    var iterator = serialized.GetIterator();
                    var dirty = false;
                    while (iterator.Next(true)) {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (iterator.propertyPath == "m_Script") continue;
                        var value = iterator.objectReferenceValue;
                        if (value != null && map.TryGetValue(value, out var replacement)) {
                            iterator.objectReferenceValue = replacement;
                            dirty = true;
                        }
                    }
                    if (dirty) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static List<Object> CollectObjectReferences(Object obj) {
            var references = new List<Object>();
            if (obj == null) return references;
            using (var serialized = new SerializedObject(obj)) {
                var iterator = serialized.GetIterator();
                while (iterator.Next(true)) {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = iterator.objectReferenceValue;
                    if (value != null) references.Add(value);
                }
            }
            return references;
        }
    }
}
