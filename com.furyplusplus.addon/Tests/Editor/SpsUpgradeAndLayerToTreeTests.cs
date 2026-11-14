using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * VRCFury 1.1443.0 split SPS upgrading into SpsPlugUpgrader (which now skips covered
     * renderers before probing their size), moved TpsConfigurer.HasDpsOrTpsMaterial to
     * TpsUtils, and rewrote LayerToTreeService's two-state conversion without the old
     * Optimize(condition, on, off) step. These run against the installed VRCFury and are
     * ignored when it is older than 1.1443.0. VRCFury 1.1459.0 also gave
     * VFClip.GetAllCurves an optional filter parameter.
     */
    public class SpsUpgradeAndLayerToTreeTests {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        private static void RequireSps3() {
            if (ReflectionUtils.FindType("VF.Builder.Haptics.TpsUtils") == null
                || ReflectionUtils.FindType("VF.Builder.Haptics.SpsPlugUpgrader") == null) {
                Assert.Ignore("VRCFury 1.1443.0 or later is not installed.");
            }
        }

        [Test]
        public void MaterialProbeResolvesFromTpsUtils() {
            RequireSps3();
            SpsCompat.DemandMaterialProbe();
            Assert.AreEqual("VF.Builder.Haptics.TpsUtils", SpsCompat.HasDpsOrTpsMaterial.DeclaringType.FullName);
            Assert.AreEqual(typeof(bool), SpsCompat.HasDpsOrTpsMaterial.ReturnType);
            CollectionAssert.AreEqual(new[] { typeof(Renderer) },
                SpsCompat.HasDpsOrTpsMaterial.GetParameters().Select(parameter => parameter.ParameterType));
        }

        [Test]
        public void CoveredRendererSkipIsSupersededByVrcfury() {
            var module = ModuleRegistry.All.OfType<SpsCoveredRendererModule>().Single();
            Assert.IsTrue(module.Superseded.HasValue);
            Assert.AreEqual("1.1443.0", module.Superseded.Value.Version);
        }

        [Test]
        public void SpsPlugUpgraderChecksCoverageBeforeSizing() {
            RequireSps3();
            // The superseded module is only safe while VRCFury itself rejects covered
            // renderers before GetAutoWorldSize; if Discover disappears, revisit it.
            var discover = ReflectionUtils.FindType("VF.Builder.Haptics.SpsPlugUpgrader")
                .GetMethod("Discover", Any);
            Assert.NotNull(discover, "SpsPlugUpgrader.Discover()");
        }

        [Test]
        public void OneSidedTogglesHookTheZeroLengthClip() {
            RequireSps3();
            ToggleTreeCompat.EnsureResolved();
            ClipCurveCompat.EnsureResolved();
            var method = ReflectionUtils.Demand(ToggleTreeCompat.Make0LengthClipForState,
                "LayerToTreeService.Make0LengthClipForState(layer, state)");
            Assert.AreEqual(ClipCurveCompat.ClipType.BaseType, method.ReturnType, "returns VFMotion");
            var parameters = method.GetParameters();
            Assert.AreEqual("VF.Utils.Controller.VFLayer", parameters[0].ParameterType.FullName);
            Assert.AreEqual("VF.Utils.Controller.VFState", parameters[1].ParameterType.FullName);
            Assert.IsNull(ReflectionUtils.FindType("VF.Service.LayerToTreeService")
                    .GetMethods(Any).FirstOrDefault(candidate => candidate.Name == "Optimize"),
                "the old Optimize(condition, on, off, tree) step is gone");
        }

        [Test]
        public void RestOnlySideIsKeptWhenTheOtherStateHasNoMotion() {
            RequireSps3();
            ToggleTreeCompat.DemandCore();
            ReflectionUtils.Demand(ToggleTreeCompat.StateMotion, "VFState.motion");
            var layer = NewLayer();
            var off = NewState(layer, "Off");
            var on = NewState(layer, "On");
            SetMotion(off, ToggleTreeCompat.NewEmptyClip("off"));

            Assert.IsFalse(OffSideEliminationPatch.OtherStatesHaveMotions(layer, off),
                "dropping Off would leave both sides empty");

            SetMotion(on, ToggleTreeCompat.NewEmptyClip("on"));
            Assert.IsTrue(OffSideEliminationPatch.OtherStatesHaveMotions(layer, off));
            Assert.IsTrue(OffSideEliminationPatch.OtherStatesHaveMotions(layer, on));
        }

        [Test]
        public void SingleStateLayersAreLeftAlone() {
            RequireSps3();
            ToggleTreeCompat.DemandCore();
            ReflectionUtils.Demand(ToggleTreeCompat.StateMotion, "VFState.motion");
            var layer = NewLayer();
            var only = NewState(layer, "Only");
            SetMotion(only, ToggleTreeCompat.NewEmptyClip("only"));
            Assert.IsFalse(OffSideEliminationPatch.OtherStatesHaveMotions(layer, only));
        }

        [Test]
        public void ClipCurvesResolveWithOrWithoutTheFilterParameter() {
            // VRCFury 1.1459.0 changed GetAllCurves() to GetAllCurves(filter = null).
            ClipCurveCompat.DemandCore();
            var parameters = ClipCurveCompat.ClipGetAllCurves.GetParameters();
            Assert.LessOrEqual(parameters.Length, 1);
            if (parameters.Length == 1) Assert.IsTrue(parameters[0].IsOptional);

            var curves = ClipCurveCompat.AllCurvesOf(ToggleTreeCompat.NewEmptyClip("curves"));
            Assert.NotNull(curves);
            Assert.AreEqual(0, curves.Length);
        }

        [Test]
        public void ClipIteratorResolvesTheInheritedFrom() {
            // Since VRCFury 1.1443.0 AnimatorIterator.Clips inherits From(VFMotion) from
            // MotionIterator<VFClip>. The fade and separate-local toggles depend on it.
            RequireSps3();
            ToggleTreeCompat.EnsureResolved();
            ReflectionUtils.Demand(ToggleTreeCompat.ClipsFromMotion, "AnimatorIterator.Clips.From(VFMotion)");
            var clip = ToggleTreeCompat.NewEmptyClip("iterated");
            var clips = ((System.Collections.IEnumerable)ReflectionUtils.InvokeUnwrapped(
                ToggleTreeCompat.ClipsFromMotion, ToggleTreeCompat.NewClipsIterator(), new[] { clip }))
                .Cast<object>().ToList();
            Assert.AreEqual(1, clips.Count);
            Assert.AreSame(clip, clips[0]);
        }

        private static object NewLayer() {
            // A plain VFController: VFAvatarController.NewLayer also needs the build's
            // LayerSourceService, and these checks only look at the layer's states.
            var controllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            var controller = controllerType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { "FuryPlusPlus test" });
            var newLayer = ReflectionUtils.FindUniqueMethod(controllerType, "NewLayer",
                method => method.GetParameters().Length == 2);
            Assert.NotNull(newLayer, "VFController.NewLayer(name, insertAt)");
            return ReflectionUtils.InvokeUnwrapped(newLayer, controller, new object[] { "Toggle", -1 });
        }

        private static object NewState(object layer, string name) {
            return ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.NewState, layer, new object[] { name });
        }

        private static void SetMotion(object state, object motion) {
            ToggleTreeCompat.StateMotion.SetValue(state, motion);
        }
    }
}
