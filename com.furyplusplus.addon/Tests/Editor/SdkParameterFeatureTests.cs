using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * Parameter features added by VRC SDK 3.10.6 and handled by VRCFury 1.1435.0:
     * face tracking blend sets read their source parameter, and the compressor refuses
     * parameters with a non-standard network encoding.
     */
    public class SdkParameterFeatureTests {
        private GameObject root;
        private AnimatorController controller;

        [TearDown]
        public void TearDown() {
            if (root != null) Object.DestroyImmediate(root);
            if (controller != null) Object.DestroyImmediate(controller);
        }

        [Test]
        public void StandardEncodingsStayCompressible() {
            CompressorCompat.EnsureResolved();
            foreach (var type in new[] {
                         VRCExpressionParameters.ValueType.Bool,
                         VRCExpressionParameters.ValueType.Int,
                         VRCExpressionParameters.ValueType.Float,
                     }) {
                Assert.IsTrue(CompressorCompat.CanCompressEncoding(
                    new VRCExpressionParameters.Parameter { name = "p", valueType = type }), type.ToString());
            }
        }

        [Test]
        public void EncodingCheckResolvesOnVrcfuryReleasesThatHaveIt() {
            var extensions = ReflectionUtils.FindType("VF.Utils.VRCExpressionParameterExtensions");
            if (extensions == null) Assert.Ignore("VRCFury not installed.");
            CompressorCompat.EnsureResolved();
            var stockHasIt = extensions.GetMethod("HasStandardNetworkEncoding",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic) != null;
            Assert.AreEqual(stockHasIt, CompressorCompat.HasStandardNetworkEncoding != null);
        }

#if VRCSDK_HAS_FACE_TRACKING
        private VRCAvatarDescriptor MakeAvatarWithFaceTrackingState(
            params VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet[] sets
        ) {
            controller = new AnimatorController();
            controller.AddLayer("FT");
            var state = controller.layers[0].stateMachine.AddState("Set");
            var behaviour = state.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>();
            behaviour.blendSets = new System.Collections.Generic.List<VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet>(sets);

            root = new GameObject("FppParamUsageTest");
            var avatar = root.AddComponent<VRCAvatarDescriptor>();
            avatar.customizeAnimationLayers = true;
            avatar.baseAnimationLayers = new[] {
                new VRCAvatarDescriptor.CustomAnimLayer {
                    type = VRCAvatarDescriptor.AnimLayerType.FX,
                    isDefault = false,
                    animatorController = controller,
                },
            };
            avatar.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
            return avatar;
        }

        [Test]
        public void FaceTrackingBlendSetSourceParameterIsARead() {
            var avatar = MakeAvatarWithFaceTrackingState(
                new VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet {
                    type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                    sourceParameter = "FtSmile",
                },
                null);
            var index = ParamUsageIndex.Build(avatar);
            Assert.IsTrue(index.Reads.Contains("FtSmile"));
        }

        [Test]
        public void FaceTrackingBlendSetWithoutParameterSourceReadsNothing() {
            var nonParameter = System.Enum.GetValues(typeof(VRC_AnimatorFaceTrackingBlendshapesSet.SourceType));
            foreach (VRC_AnimatorFaceTrackingBlendshapesSet.SourceType type in nonParameter) {
                if (type == VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter) continue;
                TearDown();
                var avatar = MakeAvatarWithFaceTrackingState(
                    new VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet { type = type, sourceParameter = "Ignored" });
                Assert.IsFalse(ParamUsageIndex.Build(avatar).Reads.Contains("Ignored"), type.ToString());
            }
        }
#endif
    }
}
