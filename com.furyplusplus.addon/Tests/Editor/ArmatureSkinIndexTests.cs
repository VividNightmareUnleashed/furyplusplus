using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus.Tests.Editor {
    public class ArmatureSkinIndexTests {
        [Test]
        public void UsageScanSeesPendingBoneRewrites() {
            var module = ModuleRegistry.Find("armatureSkinIndex");
            if (!ModuleRegistry.IsActive(module)) {
                Assert.Ignore("Armature skin module not installed (VRCFury absent or incompatible).");
            }
            ArmatureCompat.EnsureResolved();
            var master = Settings.MasterEnabled;
            var hadOverride = EditorPrefs.HasKey(module.PrefKey);
            var enabled = EditorPrefs.GetBool(module.PrefKey);
            var avatar = new GameObject("Armature skin test");
            var mesh = new Mesh {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                triangles = new[] { 0, 1, 2 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = new[] {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 }
                }
            };
            Mesh rewrittenMesh = null;
            try {
                Settings.MasterEnabled = true;
                Settings.SetModuleEnabled(module, true);
                var from = new GameObject("Unused outfit bone");
                var to = new GameObject("Avatar bone");
                from.transform.SetParent(avatar.transform);
                to.transform.SetParent(avatar.transform);
                var skin = avatar.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                skin.bones = new[] { from.transform };
                var wrappedAvatar = Wrap(avatar);
                var service = FormatterServices.GetUninitializedObject(ArmatureCompat.ArmatureLinkApply.DeclaringType);
                ArmatureCompat.ArmatureLinkAvatarField.SetValue(service, wrappedAvatar);
                InvokePatch("Begin", service);
                Assert.That(InvokePatch("RecordRewrite", Wrap(from), Wrap(to)), Is.False);
                Assert.That(skin.bones[0], Is.EqualTo(from.transform), "The rewrite should still be batched.");

                var usage = ArmatureCompat.GetUsageReasons.Invoke(null, new[] { wrappedAvatar });
                rewrittenMesh = skin.sharedMesh;
                Assert.That(skin.bones[0], Is.EqualTo(to.transform));
                var contains = ReflectionUtils.FindUniqueMethod(ArmatureCompat.GetUsageReasons.ReturnType,
                    "ContainsKey", BindingFlags.Instance | BindingFlags.Public,
                    method => method.GetParameters().Length == 1);
                Assert.That(contains, Is.Not.Null);
                Assert.That(contains.Invoke(usage, new[] { Wrap(from) }), Is.False,
                    "Pruning must not retain a bone because of an uncommitted skin reference.");
                Assert.That(contains.Invoke(usage, new[] { Wrap(to) }), Is.True);
            } finally {
                InvokePatch("End", new object[] { null });
                Settings.MasterEnabled = master;
                if (hadOverride) EditorPrefs.SetBool(module.PrefKey, enabled);
                else EditorPrefs.DeleteKey(module.PrefKey);
                Object.DestroyImmediate(avatar);
                if (rewrittenMesh != null && rewrittenMesh != mesh) Object.DestroyImmediate(rewrittenMesh);
                Object.DestroyImmediate(mesh);
            }
        }

        private static object Wrap(GameObject gameObject) {
            var wrapper = FormatterServices.GetUninitializedObject(ArmatureCompat.VfGameObjectType);
            VfGameObjectCompat.GameObjectField.SetValue(wrapper, gameObject);
            return wrapper;
        }

        private static object InvokePatch(string name, params object[] arguments) {
            return ReflectionUtils.FindUniqueMethod(typeof(ArmatureSkinIndexPatch), name,
                method => method.GetParameters().Length == arguments.Length).Invoke(null, arguments);
        }
    }
}
