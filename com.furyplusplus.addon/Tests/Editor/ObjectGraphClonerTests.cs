using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus.Tests.Editor {
    /** Synthetic node for graph tests; Object-typed fields serialize as plain references. */
    public class GraphClonerTestNode : ScriptableObject {
        public Object direct;
        public List<Object> list = new List<Object>();
    }

    /** Synthetic component modeling the replay root-swap remap. */
    public class GraphClonerTestRefHolder : MonoBehaviour {
        public GameObject target;
        public Transform targetTransform;
    }

    public class ObjectGraphClonerTests {
        private readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown() {
            foreach (var obj in created) {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            created.Clear();
        }

        private GraphClonerTestNode Node(string name) {
            var node = ScriptableObject.CreateInstance<GraphClonerTestNode>();
            node.name = name;
            created.Add(node);
            return node;
        }

        [Test]
        public void WalkDiscoversClosureThroughCloneClassifiedObjectsOnly() {
            var a = Node("a");
            var b = Node("b");
            var c = Node("c");
            var asIsLeaf = Node("leaf");
            var behindLeaf = Node("behindLeaf");
            a.direct = b;
            b.direct = c;
            a.list.Add(asIsLeaf);
            asIsLeaf.direct = behindLeaf;

            var result = ObjectGraphCloner.Walk(new Object[] { a },
                obj => obj == asIsLeaf ? ObjectGraphCloner.RefKind.AsIs : ObjectGraphCloner.RefKind.Clone);

            Assert.That(result.ToClone, Is.EqualTo(new Object[] { b, c }),
                "discovery order must be stable and must not continue through AsIs leaves");
            Assert.That(result.ToClone, Has.No.Member(behindLeaf));
            Assert.That(result.HasRejections, Is.False);
        }

        [Test]
        public void WalkClassifiesButDoesNotTraverseOpaqueObjects() {
            var a = Node("a");
            var b = Node("b");
            var c = Node("c");
            a.direct = b;
            b.direct = c;

            var result = ObjectGraphCloner.Walk(new Object[] { a },
                _ => ObjectGraphCloner.RefKind.Clone,
                obj => obj != b); // b cannot hold interesting references (e.g. a Mesh)

            Assert.That(result.ToClone, Is.EqualTo(new Object[] { b }),
                "opaque objects still join the closure; their referents do not");
        }

        [Test]
        public void WalkTerminatesOnCycles() {
            var a = Node("a");
            var b = Node("b");
            a.direct = b;
            b.direct = a;

            var result = ObjectGraphCloner.Walk(new Object[] { a },
                _ => ObjectGraphCloner.RefKind.Clone);

            // b via a, then a again via b (roots are traversed, not classified, but a
            // re-discovered as a reference is classified like any other).
            Assert.That(result.ToClone, Is.EquivalentTo(new Object[] { a, b }));
        }

        [Test]
        public void WalkSurfacesRejections() {
            var a = Node("a");
            var external = Node("external");
            a.direct = external;

            var result = ObjectGraphCloner.Walk(new Object[] { a },
                _ => ObjectGraphCloner.RefKind.Reject);

            Assert.That(result.HasRejections, Is.True);
            Assert.That(result.Rejected, Is.EqualTo(new Object[] { external }));
            Assert.That(result.ToClone, Is.Empty);
        }

        [Test]
        public void CloneAllAndRemapRewireCopiesAndLeaveOriginalsUntouched() {
            var a = Node("a");
            var b = Node("b");
            a.direct = b;
            a.list.Add(b);
            a.list.Add(null);
            b.direct = a;

            var map = ObjectGraphCloner.CloneAll(new Object[] { a, b });
            foreach (var copy in map.Values) created.Add(copy);
            ObjectGraphCloner.Remap(map.Values, map);

            var copyA = (GraphClonerTestNode)map[a];
            var copyB = (GraphClonerTestNode)map[b];
            Assert.That(copyA.name, Is.EqualTo("a"), "no (Clone) suffix");
            Assert.That(copyA.direct, Is.SameAs(copyB));
            Assert.That(copyA.list[0], Is.SameAs(copyB));
            Assert.That(copyA.list[1], Is.Null, "null references stay null");
            Assert.That(copyB.direct, Is.SameAs(copyA), "cycles remap in both directions");
            Assert.That(a.direct, Is.SameAs(b), "original untouched");
            Assert.That(b.direct, Is.SameAs(a), "original untouched");
        }

        [Test]
        public void RemapRewiresGameObjectAndComponentReferences() {
            var oldRoot = new GameObject("oldRoot");
            var newRoot = new GameObject("newRoot");
            var holderGo = new GameObject("holder");
            created.Add(oldRoot);
            created.Add(newRoot);
            created.Add(holderGo);
            var holder = holderGo.AddComponent<GraphClonerTestRefHolder>();
            holder.target = oldRoot;
            holder.targetTransform = oldRoot.transform;

            // Models the replay root-swap: shell root/Transform → restored obj equivalents.
            var map = new Dictionary<Object, Object> {
                { oldRoot, newRoot },
                { oldRoot.transform, newRoot.transform },
            };
            ObjectGraphCloner.Remap(new Object[] { holder }, map);

            Assert.That(holder.target, Is.SameAs(newRoot));
            Assert.That(holder.targetTransform, Is.SameAs(newRoot.transform));
        }
    }
}
