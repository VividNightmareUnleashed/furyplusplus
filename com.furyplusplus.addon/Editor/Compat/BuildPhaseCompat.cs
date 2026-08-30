using System;
using System.Reflection;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus {
    /** VRCFury action-order and injector surface behind mid-build callbacks. */
    internal static class BuildPhaseCompat {
        private static Type featureOrderType;
        private static MethodInfo actionGetPriority;
        private static MethodInfo actionGetService;
        private static MethodInfo getInjector;
        private static MethodInfo injectorGetService;
        private static Type injectorContextType;

        internal static void Resolve(VrcfuryCompat compat) {
            featureOrderType = ReflectionUtils.Demand(
                compat.AvatarsEditorAssembly.GetType("VF.Feature.Base.FeatureOrder", false),
                "VF.Feature.Base.FeatureOrder");
            var actionType = ReflectionUtils.Demand(
                compat.AvatarsEditorAssembly.GetType("VF.Feature.Base.FeatureBuilderAction", false),
                "VF.Feature.Base.FeatureBuilderAction");
            actionGetPriority = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(actionType, "GetPriorty",
                    method => method.GetParameters().Length == 0 && method.ReturnType.IsEnum),
                "FeatureBuilderAction.GetPriorty()");
            actionGetService = compat.ActionGetService;

            var injectorBuilderType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Builder.VRCFuryInjectorBuilder"),
                "VF.Builder.VRCFuryInjectorBuilder");
            getInjector = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(injectorBuilderType, "GetInjector",
                    method => method.IsStatic && method.GetParameters().Length == 1),
                "VRCFuryInjectorBuilder.GetInjector(...)");
            var injectorType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Injector.VRCFuryInjector"),
                "VF.Injector.VRCFuryInjector");
            injectorGetService = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(injectorType, "GetService",
                    method => method.GetParameters().Length == 2
                              && method.GetParameters()[0].ParameterType == typeof(Type)),
                "VRCFuryInjector.GetService(Type, Context)");
            injectorContextType = injectorGetService.GetParameters()[1].ParameterType;
        }

        internal static int GetThreshold(string phaseName) {
            try {
                return Convert.ToInt32(Enum.Parse(featureOrderType, phaseName));
            } catch (Exception) {
                throw new MissingMemberException(
                    "VRCFury member not found: FeatureOrder." + phaseName);
            }
        }

        internal static int GetPriority(object action) {
            return Convert.ToInt32(actionGetPriority.Invoke(action, null));
        }

        internal static object GetActionService(object action) {
            return actionGetService.Invoke(action, null);
        }

        internal static object GetService(VRCAvatarDescriptor descriptor, string typeFullName) {
            if (descriptor == null) return null;
            var injector = getInjector.Invoke(null, new object[] { descriptor });
            var serviceType = ReflectionUtils.FindType(typeFullName);
            if (injector == null || serviceType == null) return null;
            return injectorGetService.Invoke(injector, new[] {
                serviceType, Activator.CreateInstance(injectorContextType)
            });
        }
    }
}
