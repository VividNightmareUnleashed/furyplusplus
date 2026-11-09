using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * VRCFury 1.1435.0 appended `bool forceClone = false` to RendererExtensions.GetMutableMesh
     * and ObjectExtensions.Clone<T>. Lookups must accept the old and new shapes, and calls
     * must pass the declared default so behavior matches VRCFury's own call sites.
     */
    public class TrailingOptionalParameterTests {
        private static class OldShape {
            internal static string Target(GameObject obj, string reason) => "old:" + reason;
        }

        private static class NewShape {
            internal static string Target(GameObject obj, string reason, bool forceClone = false) =>
                "new:" + reason + ":" + forceClone;
        }

        private static class RequiredExtra {
            internal static string Target(GameObject obj, string reason, bool forceClone) => "required";
        }

        [Test]
        public void LeadingSignatureMatchesOldAndNewShapes() {
            Assert.NotNull(ReflectionUtils.FindMethodWithLeadingSignature(
                typeof(OldShape), "Target", typeof(string), typeof(GameObject), typeof(string)));
            Assert.NotNull(ReflectionUtils.FindMethodWithLeadingSignature(
                typeof(NewShape), "Target", typeof(string), typeof(GameObject), typeof(string)));
        }

        [Test]
        public void LeadingSignatureRejectsRequiredExtraParameter() {
            Assert.IsNull(ReflectionUtils.FindMethodWithLeadingSignature(
                typeof(RequiredExtra), "Target", typeof(string), typeof(GameObject), typeof(string)));
        }

        [Test]
        public void PadOptionalArgsPassesDeclaredDefaults() {
            var method = ReflectionUtils.FindMethodWithLeadingSignature(
                typeof(NewShape), "Target", typeof(string), typeof(GameObject), typeof(string));
            var args = ReflectionUtils.PadOptionalArgs(method, new object[] { null, "why" });
            Assert.AreEqual(3, args.Length);
            Assert.AreEqual(false, args[2]);
            Assert.AreEqual("new:why:False", method.Invoke(null, args));
        }

        [Test]
        public void PadOptionalArgsLeavesExactArgumentListsAlone() {
            var method = ReflectionUtils.FindMethodWithLeadingSignature(
                typeof(OldShape), "Target", typeof(string), typeof(GameObject), typeof(string));
            var args = new object[] { null, "why" };
            Assert.AreSame(args, ReflectionUtils.PadOptionalArgs(method, args));
        }

        [Test]
        public void InstalledVrcfuryMutableMeshLookupsResolve() {
            if (ReflectionUtils.FindType("VF.Utils.RendererExtensions") == null) {
                Assert.Ignore("VRCFury not installed.");
            }
            ArmatureCompat.EnsureResolved();
            BlendshapeOptimizerCompat.EnsureResolved();
            Assert.NotNull(ArmatureCompat.GetMutableMesh, "RendererExtensions.GetMutableMesh");
            Assert.NotNull(BlendshapeOptimizerCompat.SkinGetMutableMesh, "GetMutableMesh (blendshape)");
        }

#if VRCSDK_HAS_FACE_TRACKING
        private GameObject root;

        [TearDown]
        public void TearDown() {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
        }

        private (SkinnedMeshRenderer Skin, VRCAvatarDescriptor Avatar) MakeFaceTrackingAvatar() {
            root = new GameObject("FppFaceTrackingTest");
            var avatar = root.AddComponent<VRCAvatarDescriptor>();
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform);
            var skin = body.AddComponent<SkinnedMeshRenderer>();
            avatar.enableFaceTracking = true;
            avatar.faceTrackingMeshes = new System.Collections.Generic.List<SkinnedMeshRenderer> { skin };
            avatar.faceTrackingBlendshapesSetList =
                new System.Collections.Generic.List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet> {
                    null,
                    new VRCAvatarDescriptor.FaceTrackingBlendshapesSet {
                        faceTrackingBlendshapeNames = new[] { "JawOpen", "", null },
                        visemeBlendshapeNames = new[] { "vrc.v_aa" },
                    },
                };
            avatar.faceTrackingParameterBehaviors =
                new System.Collections.Generic.List<VRCAvatarDescriptor.FaceTrackingParameterBehavior> {
                    new VRCAvatarDescriptor.FaceTrackingParameterBehavior {
                        targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.ApplyToBlendshape,
                        targetBlendshapeName = "TongueOut",
                    },
                    new VRCAvatarDescriptor.FaceTrackingParameterBehavior {
                        targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.LimitBlendshapeMax,
                        targetBlendshapeName = "CheekPuff",
                    },
                    new VRCAvatarDescriptor.FaceTrackingParameterBehavior {
                        targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.LimitBlendshapeMin,
                        targetBlendshapeName = "MouthClosed",
                    },
                    new VRCAvatarDescriptor.FaceTrackingParameterBehavior {
                        targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator,
                        targetBlendshapeName = "NotABlendshapeTarget",
                    },
                };
            return (skin, avatar);
        }

        [Test]
        public void FaceTrackingBlendshapesMatchVrcfury() {
            var (skin, avatar) = MakeFaceTrackingAvatar();
            CollectionAssert.AreEquivalent(
                new[] { "JawOpen", "vrc.v_aa", "TongueOut", "CheekPuff", "MouthClosed" },
                BlendshapeBakeRewritePatch.GetFaceTrackingBlendshapeNames(skin, avatar).ToArray());
        }

        [Test]
        public void FaceTrackingBlendshapesNeedEnabledFaceTrackingOnThatMesh() {
            var (skin, avatar) = MakeFaceTrackingAvatar();
            avatar.enableFaceTracking = false;
            CollectionAssert.IsEmpty(BlendshapeBakeRewritePatch.GetFaceTrackingBlendshapeNames(skin, avatar));

            avatar.enableFaceTracking = true;
            avatar.faceTrackingMeshes.Clear();
            CollectionAssert.IsEmpty(BlendshapeBakeRewritePatch.GetFaceTrackingBlendshapeNames(skin, avatar));
        }

        [Test]
        public void InstalledVrcfuryFaceTrackingSupportIsDetected() {
            var builder = ReflectionUtils.FindType("VF.Feature.BlendshapeOptimizerBuilder");
            if (builder == null) Assert.Ignore("VRCFury not installed.");
            BlendshapeOptimizerCompat.EnsureResolved();
            var stockHasIt = builder.GetMethods(System.Reflection.BindingFlags.Instance
                                                | System.Reflection.BindingFlags.Static
                                                | System.Reflection.BindingFlags.NonPublic
                                                | System.Reflection.BindingFlags.Public)
                .Any(method => method.Name == "GetFaceTrackingBlendshapeNames");
            Assert.AreEqual(stockHasIt, BlendshapeOptimizerCompat.StockKeepsFaceTrackingBlendshapes);
        }
#endif
    }
}
