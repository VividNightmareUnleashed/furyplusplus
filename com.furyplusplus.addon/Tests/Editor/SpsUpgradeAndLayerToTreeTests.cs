using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * VRCFury 1.1443.0 split SPS upgrading into SpsPlugUpgrader (which now skips covered
     * renderers before probing their size), moved TpsConfigurer.HasDpsOrTpsMaterial to
     * TpsUtils, and rewrote LayerToTreeService's two-state conversion without the old
     * Optimize(condition, on, off) step. These run against the installed VRCFury and are
     * ignored when it is older than 1.1443.0.
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

        private static object NewLayer() {
            var controllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            var controller = controllerType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { "FuryPlusPlus test" });
            var avatarControllerType = ReflectionUtils.FindType("VF.Utils.VFAvatarController");
            var constructor = avatarControllerType.GetConstructors(Any)
                .Single(ctor => ctor.GetParameters().Length == 7);
            var fx = constructor.Invoke(new object[] {
                controller,
                VRCAvatarDescriptor.AnimLayerType.FX,
                (Func<int>)(() => 7),
                (Func<string>)(() => ""),
                (Func<string, string>)(name => "VF7_" + name),
                null,
                null
            });
            return ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.NewLayer, fx, new object[] { "Toggle", -1 });
        }

        private static object NewState(object layer, string name) {
            return ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.NewState, layer, new object[] { name });
        }

        private static void SetMotion(object state, object motion) {
            ToggleTreeCompat.StateMotion.SetValue(state, motion);
        }
    }
}
