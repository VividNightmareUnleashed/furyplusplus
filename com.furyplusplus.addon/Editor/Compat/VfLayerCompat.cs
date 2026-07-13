using System.Reflection;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for the VF.Utils.Controller.VFLayer members shared by the two
     * modules that patch its allBehaviourContainers getter. Members stay null when they
     * fail to resolve; consuming modules throw from their own Install checks (fail-closed).
     */
    internal static class VfLayerCompat {
        private static bool resolved;

        /** VFLayer's backing AnimatorStateMachine field; the identity key for per-layer caches. */
        internal static FieldInfo RootStateMachineField { get; private set; }

        /** The nonpublic allBehaviourContainers property getter (the shared patched getter). */
        internal static MethodInfo BehaviourContainersGetter { get; private set; }

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            var vfLayerType = ReflectionUtils.FindType("VF.Utils.Controller.VFLayer");
            RootStateMachineField = vfLayerType?
                .GetField("rootStateMachine", BindingFlags.Instance | BindingFlags.NonPublic);
            BehaviourContainersGetter = vfLayerType?
                .GetProperty("allBehaviourContainers", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetGetMethod(true);
        }
    }
}
