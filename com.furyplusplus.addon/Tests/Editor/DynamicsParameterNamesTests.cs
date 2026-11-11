using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * Every animator parameter a PhysBone, contact receiver or VRC Raycast writes must count
     * as dynamics-driven, including the advanced contact parameters of VRC SDK 3.10.6 (which
     * VRCFury 1.1443.0 uses for SPS). The Int-narrowing guard and the keep-dynamics option
     * of the unused-parameter strip both read this set.
     */
    public class DynamicsParameterNamesTests {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown() {
            foreach (var gameObject in created) {
                if (gameObject != null) Object.DestroyImmediate(gameObject);
            }
            created.Clear();
        }

        [Test]
        public void ReceiverWithEveryAdvancedGroupListsAllOutputs() {
            var receiver = AddReceiver("Poke", "All", "Finger", "Hand", " ", "");
            CollectionAssert.AreEquivalent(new[] {
                "Poke",
                "Poke_SenderType",
                "Poke_SenderLocalPositionX", "Poke_SenderLocalPositionY", "Poke_SenderLocalPositionZ",
                "Poke_SenderLossyScaleX", "Poke_SenderLossyScaleY", "Poke_SenderLossyScaleZ",
                "Poke_SenderVelocityX", "Poke_SenderVelocityY", "Poke_SenderVelocityZ",
                "Poke_SenderVelocityMagnitude",
                "Poke_PointSeparation",
                "Poke_ProximitySpeed",
                "Poke_CollisionTag_Finger", "Poke_CollisionTag_Hand",
            }, AvatarPipelineCompat.GetDynamicsParameterNames(receiver));
        }

        [Test]
        public void ReceiverListsOnlyTheSelectedGroups() {
            var receiver = AddReceiver("Poke", "SenderType, PointSeparation", "Finger");
            CollectionAssert.AreEquivalent(
                new[] { "Poke", "Poke_SenderType", "Poke_PointSeparation" },
                AvatarPipelineCompat.GetDynamicsParameterNames(receiver),
                "collision tags need their own flag");
        }

        [Test]
        public void ReceiverWithoutAdvancedParametersListsItsParameter() {
            var receiver = AddReceiver("Poke", "None", "Finger");
            CollectionAssert.AreEquivalent(new[] { "Poke" },
                AvatarPipelineCompat.GetDynamicsParameterNames(receiver));
        }

        [Test]
        public void ReceiverWithoutParameterWritesNothing() {
            var receiver = AddReceiver("", "All", "Finger");
            CollectionAssert.IsEmpty(AvatarPipelineCompat.GetDynamicsParameterNames(receiver));
        }

        [Test]
        public void RaycastListsHitRatioAndDistance() {
            var type = ReflectionUtils.FindType("VRC.SDK3.Avatars.Components.VRCRaycast");
            if (type == null) Assert.Ignore("This VRChat SDK has no VRCRaycast.");
            var raycast = NewObject("Raycast").AddComponent(type);
            type.GetProperty("Parameter", Instance).SetValue(raycast, "Ray");
            CollectionAssert.AreEquivalent(new[] { "Ray_Hit", "Ray_Ratio", "Ray_Distance" },
                AvatarPipelineCompat.GetDynamicsParameterNames(raycast));
        }

        [Test]
        public void PhysBoneListsItsFiveOutputs() {
            var type = RequireType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            var physBone = NewObject("PhysBone").AddComponent(type);
            type.GetField("parameter", Instance).SetValue(physBone, "Tail");
            CollectionAssert.AreEquivalent(new[] {
                "Tail_IsGrabbed", "Tail_IsPosed", "Tail_Angle", "Tail_Stretch", "Tail_Squish"
            }, AvatarPipelineCompat.GetDynamicsParameterNames(physBone));
        }

        [Test]
        public void OtherComponentsWriteNothing() {
            CollectionAssert.IsEmpty(AvatarPipelineCompat.GetDynamicsParameterNames(NewObject("Plain").transform));
            CollectionAssert.IsEmpty(AvatarPipelineCompat.GetDynamicsParameterNames(null));
        }

        [Test]
        public void SenderTypeIntIsNotNarrowed() {
            var avatar = NewObject("Avatar");
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            var receiver = AddReceiver("Poke", "SenderType");
            receiver.transform.SetParent(avatar.transform);

            var index = ParamUsageIndex.Build(descriptor);
            CollectionAssert.IsSupersetOf(index.DynamicsParams, new[] { "Poke", "Poke_SenderType" });

            // A menu toggle that also writes 0/1 would otherwise make it look narrowable,
            // but the receiver writes 0 to 3 here.
            var parameter = new VRCExpressionParameters.Parameter {
                name = "Poke_SenderType", valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true
            };
            index.Details[parameter.name] = new ParamUsageIndex.ParamDetail { HasMenuControl = true };
            Assert.AreEqual(NarrowIntParamsPass.Verdict.Ineligible,
                NarrowIntParamsPass.Classify(parameter, index, new List<Regex>()));
        }

        private Component AddReceiver(string parameter, string flags, params string[] tags) {
            var type = RequireType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver");
            var flagsField = type.GetField("advancedParameters", Instance);
            if (flagsField == null || !flagsField.FieldType.IsEnum) {
                Assert.Ignore("This VRChat SDK has no advanced contact parameters.");
            }
            var receiver = NewObject("Receiver").AddComponent(type);
            type.GetField("parameter", Instance).SetValue(receiver, parameter);
            flagsField.SetValue(receiver, Enum.Parse(flagsField.FieldType, flags));
            type.GetField("collisionTags", Instance).SetValue(receiver, tags.ToList());
            return receiver;
        }

        private static Type RequireType(string fullName) {
            var type = ReflectionUtils.FindType(fullName);
            if (type == null) Assert.Ignore(fullName + " is not available.");
            return type;
        }

        private GameObject NewObject(string name) {
            var gameObject = new GameObject(name);
            created.Add(gameObject);
            return gameObject;
        }
    }
}
