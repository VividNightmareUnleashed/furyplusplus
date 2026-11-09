using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus.Tests.Editor {
    /**
     * VRCFury 1.1437.0 moved animator parameters into its own VFAParam model, renamed
     * ControllerManager to VFAvatarController, replaced the always-true bool with
     * `One > 0`, and moved expression parameters behind ExpressionParamsService.
     * These run against the installed VRCFury and are ignored when it is absent or older.
     */
    public class ControllerParameterModelTests {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        private static Type vfControllerType;
        private static Type vfaParamType;

        private static void RequireParameterModel() {
            vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            vfaParamType = ReflectionUtils.FindType("VF.Utils.Controller.VFAParam");
            if (vfControllerType == null || ReflectionUtils.FindType("VF.Utils.VFAvatarController") == null) {
                Assert.Ignore("VRCFury 1.1437.0 or later is not installed.");
            }
        }

        private static object NewController() {
            var create = vfControllerType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public);
            return create.Invoke(null, new object[] { "FuryPlusPlus test" });
        }

        private static object GetOrCreate(object controller, string method, string name, float def) {
            return controller.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public)
                .Invoke(controller, new[] { name, ToggleTreeCompat.FloatOrBool(def) });
        }

        /** VFAvatarController around an empty controller; One()/Always() need no services. */
        private static object NewAvatarController() {
            var avatarControllerType = ReflectionUtils.FindType("VF.Utils.VFAvatarController");
            var constructor = avatarControllerType.GetConstructors(Any)
                .Single(ctor => ctor.GetParameters().Length == 7);
            return constructor.Invoke(new object[] {
                NewController(),
                VRCAvatarDescriptor.AnimLayerType.FX,
                (Func<int>)(() => 7),
                (Func<string>)(() => ""),
                (Func<string, string>)(name => "VF7_" + name),
                null,
                null
            });
        }

        [Test]
        public void ChangedModulesResolveAgainstInstalledVrcfury() {
            RequireParameterModel();
            ToggleTreeCompat.DemandCore();
            CompressorCompat.DemandCore();
            CompressorSolverPatch.Resolve();
            Sub8Surgery.Resolve();
            ReflectionUtils.Demand(CompressorCompat.SolverGetParamsUsedInMenu,
                "ParameterCompressorSolverService.GetParamsUsedInMenu(types)");
            ReflectionUtils.Demand(CompressorCompat.SolverMenuService, "ParameterCompressorSolverService.menuService");
            ReflectionUtils.Demand(CompressorCompat.MenuGetReadOnly, "MenuService.GetReadOnlyMenu()");
            Assert.AreEqual(
                typeof(System.Collections.Generic.IEnumerable<VRCExpressionParameters.Parameter>),
                CompressorCompat.SolverPrivateSolve.GetParameters()[0].ParameterType,
                "eligibility widening reads the solver's parameter entries");
        }

        [Test]
        public void FindParameterReadsTypeAndDefault() {
            RequireParameterModel();
            ToggleTreeCompat.DemandParameterLookup();
            var controller = NewController();
            GetOrCreate(controller, "GetOrCreateBool", "Toggle", 1f);
            GetOrCreate(controller, "GetOrCreateFloat", "Radial", 0.25f);
            GetOrCreate(controller, "GetOrCreateInt", "Mode", 3f);

            var toggle = ToggleTreeCompat.FindParameter(controller, "Toggle");
            Assert.AreEqual(AnimatorControllerParameterType.Bool, toggle.type);
            Assert.IsTrue(toggle.defaultBool);
            var radial = ToggleTreeCompat.FindParameter(controller, "Radial");
            Assert.AreEqual(AnimatorControllerParameterType.Float, radial.type);
            Assert.AreEqual(0.25f, radial.defaultFloat);
            var mode = ToggleTreeCompat.FindParameter(controller, "Mode");
            Assert.AreEqual(AnimatorControllerParameterType.Int, mode.type);
            Assert.AreEqual(3, mode.defaultInt);

            Assert.IsNull(ToggleTreeCompat.FindParameter(controller, "Missing"));
            Assert.IsNull(ToggleTreeCompat.FindParameter(controller, null));
            CollectionAssert.AreEquivalent(
                new[] { "Toggle", "Radial", "Mode" },
                ToggleTreeCompat.ParametersOf(controller).Select(parameter => parameter.name));
        }

        [Test]
        public void FindParameterReturnsACopy() {
            RequireParameterModel();
            ToggleTreeCompat.DemandParameterLookup();
            var controller = NewController();
            GetOrCreate(controller, "GetOrCreateBool", "Toggle", 0f);

            ToggleTreeCompat.FindParameter(controller, "Toggle").defaultBool = true;
            Assert.IsFalse(ToggleTreeCompat.FindParameter(controller, "Toggle").defaultBool);
        }

        [Test]
        public void DetachedFloatCarriesNameAndDefault() {
            RequireParameterModel();
            ToggleTreeCompat.DemandDetachedParam();
            var parameter = ToggleTreeCompat.MakeVfaFloat("Fade", 1f);
            Assert.AreEqual("Fade", ToggleTreeCompat.ParamName(parameter));
            Assert.AreEqual(AnimatorControllerParameterType.Float,
                vfaParamType.GetProperty("type", Any).GetValue(parameter));
            Assert.AreEqual(1f, vfaParamType.GetMethod("GetDefault", Any).Invoke(parameter, null));
            Assert.IsNull(vfaParamType.GetProperty("Controller", Any).GetValue(parameter),
                "a stand-in must not join any controller");
        }

        [Test]
        public void EqualsSelectBuildsAMotion() {
            RequireParameterModel();
            ToggleTreeCompat.DemandCore();
            var off = ToggleTreeCompat.NewEmptyClip("off");
            var on = ToggleTreeCompat.NewEmptyClip("on");
            var motion = ToggleTreeCompat.EqualsSelect("Mode", 0f, off, on);
            Assert.NotNull(motion);
            Assert.IsTrue(ToggleTreeCompat.MotionType.IsInstanceOfType(motion));
        }

        [Test]
        public void FadeMatcherAcceptsVrcfuryAlways() {
            RequireParameterModel();
            ToggleTreeCompat.DemandCore();
            var controller = NewAvatarController();
            var always = controller.GetType().GetMethod("Always", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(controller, null);
            var transitions = (AnimatorCondition[][])always.GetType().GetField("transitions").GetValue(always);
            Assert.AreEqual(1, transitions.Length, "Always() is one transition");

            AnimatorControllerParameter Find(string name) => ToggleTreeCompat.FindParameter(controller, name);
            Assert.IsTrue(ToggleFadePass.IsAlwaysCondition(transitions[0], Find));

            var one = transitions[0][0].parameter;
            Assert.IsFalse(ToggleFadePass.IsAlwaysCondition(new[] {
                new AnimatorCondition { mode = AnimatorConditionMode.Less, parameter = one, threshold = 0 }
            }, Find), "Never() is not Always()");
            GetOrCreate(controller, "GetOrCreateFloat", "VF_5_One", 0f);
            Assert.IsFalse(ToggleFadePass.IsAlwaysCondition(new[] {
                new AnimatorCondition { mode = AnimatorConditionMode.Greater, parameter = "VF_5_One", threshold = 0 }
            }, Find), "a zero default is not always on");
            Assert.IsFalse(ToggleFadePass.IsAlwaysCondition(new[] {
                new AnimatorCondition { mode = AnimatorConditionMode.Greater, parameter = "VF_6_One", threshold = 0 }
            }, Find), "an undeclared parameter is not always on");
        }

        [Test]
        public void Sub8AapAndOneUseAvatarControllerNaming() {
            RequireParameterModel();
            Sub8Surgery.Resolve();
            var controller = NewAvatarController();
            var aap = ReflectionUtils.InvokeUnwrapped(CompressorCompat.CreateUniqueFloat, controller,
                new[] { "FPP/Sub8/A+B", ToggleTreeCompat.FloatOrBool(17f) });
            Assert.AreEqual("VF7_FPP/Sub8/A+B", ToggleTreeCompat.ParamName(aap));
            var saved = ToggleTreeCompat.FindParameter(controller, "VF7_FPP/Sub8/A+B");
            Assert.AreEqual(AnimatorControllerParameterType.Float, saved.type);
            Assert.AreEqual(17f, saved.defaultFloat);

            var one = ReflectionUtils.InvokeUnwrapped(ToggleTreeCompat.ControllerOne, controller, null);
            var oneName = ToggleTreeCompat.ParamName(one);
            StringAssert.IsMatch(ToggleTreeCompat.AlwaysOneParamName.ToString(), oneName);
            Assert.AreEqual(1f, ToggleTreeCompat.FindParameter(controller, oneName).defaultFloat);
        }

        [Test]
        public void SolverReadsExpressionParamsSnapshot() {
            RequireParameterModel();
            CompressorSolverPatch.Resolve();
            Assert.AreEqual(typeof(VRCExpressionParameters.Parameter[]),
                CompressorCompat.ExpressionParamsSnapshot.ReturnType);
            Assert.AreEqual(CompressorCompat.ExpressionParamsSnapshot.DeclaringType,
                CompressorCompat.SolverExpressionParams.FieldType);
            Assert.Greater((int)CompressorCompat.GetMaxCost.Invoke(null, null), 0);
        }
    }
}
