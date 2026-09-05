using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace FuryPlusPlus.Tests.Editor {
    public class OptimizationGuardTests {
        [Test]
        public void NoOpStrippingRecognizesClipsSharedWithAdditiveLayers() {
            NoOpCurveStripPass.Resolve();
            var controllerType = ClipCurveCompat.ControllerGetLayers.DeclaringType;
            var controller = ReflectionUtils.FindUniqueMethod(controllerType, "Create",
                method => method.IsStatic && method.GetParameters().Length == 1)
                .Invoke(null, new object[] { "Test" });
            var newLayer = ReflectionUtils.FindUniqueMethod(controllerType, "NewLayer",
                method => method.GetParameters().Length == 2);
            var clip = ToggleTreeCompat.NewEmptyClip("Shared");
            var overrideOnly = ToggleTreeCompat.NewEmptyClip("Override only");
            for (var i = 0; i < 3; i++) {
                var layer = newLayer.Invoke(controller, new object[] { "Layer " + i, -1 });
                ToggleTreeCompat.LayerBlendingMode.SetValue(layer, i == 1
                    ? UnityEditor.Animations.AnimatorLayerBlendingMode.Additive
                    : UnityEditor.Animations.AnimatorLayerBlendingMode.Override);
                var state = ToggleTreeCompat.NewState.Invoke(layer, new object[] { "State" });
                ToggleTreeCompat.StateWithAnimation.Invoke(state, new[] { i == 2 ? overrideOnly : clip });
            }
            CollectionAssert.AreEqual(new[] { clip }, NoOpCurveStripPass.AdditiveClipsFrom(controller));
        }

        [Test]
        public void ClipEventsCannotAliasThroughFieldSeparators() {
            var left = new AnimationClip();
            var right = new AnimationClip();
            try {
                AnimationUtility.SetAnimationEvents(left, new[] {
                    new AnimationEvent { functionName = "a|0|0|b", stringParameter = "c" }
                });
                AnimationUtility.SetAnimationEvents(right, new[] {
                    new AnimationEvent { functionName = "a", stringParameter = "b|0|0|c" }
                });
                var leftKey = new System.Text.StringBuilder();
                var rightKey = new System.Text.StringBuilder();
                ClipContentKey.AppendClipFacts(leftKey, left);
                ClipContentKey.AppendClipFacts(rightKey, right);
                Assert.That(leftKey.ToString(), Is.Not.EqualTo(rightKey.ToString()));
            } finally {
                Object.DestroyImmediate(left);
                Object.DestroyImmediate(right);
            }
        }

        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void PhaseHooksRespectMasterAndModuleSwitches(bool master, bool enabled, bool expected) {
            var module = ModuleRegistry.Find("noOpCurveStrip");
            if (!ModuleRegistry.IsActive(module)) Assert.Ignore("Requires an approved VRCFury pair.");
            var oldMaster = Settings.MasterEnabled;
            var had = EditorPrefs.HasKey(module.PrefKey);
            var previous = EditorPrefs.GetBool(module.PrefKey);
            try {
                Settings.MasterEnabled = master;
                Settings.SetModuleEnabled(module, enabled);
                var called = false;
                BuildPhaseHooks.Fire(new BuildPhaseHooks.Hook {
                    ModuleId = module.Id, Callback = _ => called = true
                }, null);
                Assert.That(called, Is.EqualTo(expected));
            } finally {
                Settings.MasterEnabled = oldMaster;
                if (had) EditorPrefs.SetBool(module.PrefKey, previous);
                else EditorPrefs.DeleteKey(module.PrefKey);
            }
        }

        [Test]
        public void LayerMergeKeepsInterveningOverridesAndRejectsOverlappingDonors() {
            var bindings = new[] {
                new HashSet<object> { "a" }, new HashSet<object> { "b" },
                new HashSet<object> { "b" }, new HashSet<object> { "c" },
                new HashSet<object> { "c" }, new HashSet<object> { "d" }
            };
            Assert.That(DbtConsolidationPass.SelectDonors(bindings, new HashSet<int> { 0, 2, 3, 4, 5 }, 0),
                Is.EqualTo(new[] { 3, 5 }));
        }

        [TestCase("speed", 0f)]
        [TestCase("speed", -1f)]
        [TestCase("timeParameterActive", true)]
        [TestCase("speedParameterActive", true)]
        [TestCase("mirror", true)]
        [TestCase("mirrorParameterActive", true)]
        [TestCase("cycleOffset", 0.5f)]
        [TestCase("cycleOffsetParameterActive", true)]
        public void ConversionRejectsStatePlaybackOverrides(string property, object value) {
            ToggleTreeCompat.EnsureResolved();
            if (ToggleTreeCompat.StateSpeed == null) Assert.Ignore("VRCFury state model unavailable.");
            var state = FormatterServices.GetUninitializedObject(ToggleTreeCompat.StateSpeed.DeclaringType);
            ToggleTreeCompat.StateSpeed.SetValue(state, 1f);
            Assert.That(ToggleConversionRuntime.HasDefaultPlayback(state), Is.True);
            var members = new[] { ToggleTreeCompat.StateSpeed, ToggleTreeCompat.StateTimeParamActive,
                ToggleTreeCompat.StateSpeedParamActive, ToggleTreeCompat.StateMirror,
                ToggleTreeCompat.StateMirrorParamActive, ToggleTreeCompat.StateCycleOffset,
                ToggleTreeCompat.StateCycleOffsetParamActive };
            Array.Find(members, member => member.Name == property).SetValue(state, value);
            Assert.That(ToggleConversionRuntime.HasDefaultPlayback(state), Is.False);
        }

        [Test]
        public void DynamicsOutputCannotBeNarrowedEvenWhenAMenuAlsoWritesIt() {
            var parameter = new VRCExpressionParameters.Parameter {
                name = "Contact", valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true
            };
            var index = new ParamUsageIndex();
            index.Details[parameter.name] = new ParamUsageIndex.ParamDetail { HasMenuControl = true };
            index.Reads.Add(parameter.name);
            Assert.That(NarrowIntParamsPass.Classify(parameter, index, new List<Regex>()),
                Is.EqualTo(NarrowIntParamsPass.Verdict.Eligible));
            index.DynamicsParams.Add(parameter.name);
            Assert.That(NarrowIntParamsPass.Classify(parameter, index, new List<Regex>()),
                Is.EqualTo(NarrowIntParamsPass.Verdict.Ineligible));
        }

        [TestCase(1f, true, 0.02f)]
        [TestCase(1f, false, 0.0002f)]
        [TestCase(25f, true, 0.5f)]
        [TestCase(75f, true, 1.5f)]
        public void BlendshapeInterpolationUsesPercentOnlyOnce(float weight, bool fix, float expected) {
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            try {
                var zero = new[] { Vector3.zero };
                mesh.AddBlendShapeFrame("Shape", 50, new[] { Vector3.right }, zero, zero);
                mesh.AddBlendShapeFrame("Shape", 100, new[] { Vector3.right * 2 }, zero, zero);
                var vertices = new[] { Vector3.zero };
                var args = new object[] { mesh, 0, 2, weight, fix, vertices, new Vector3[1],
                    new Vector4[1], new Vector3[1], new Vector3[1], new Vector3[1], false };
                ReflectionUtils.FindUniqueMethod(typeof(BlendshapeBakeRewritePatch), "BakeShape",
                    method => method.GetParameters().Length == args.Length).Invoke(null, args);
                Assert.That(vertices[0].x, Is.EqualTo(expected).Within(0.000001f));
            } finally { Object.DestroyImmediate(mesh); }
        }
    }
}
