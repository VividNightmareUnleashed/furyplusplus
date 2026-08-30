using System.Reflection;
using UnityEditor;

namespace FuryPlusPlus {
    /** Reflected Unity/VRCFury progress-window entry points used by cosmetic patches. */
    internal static class ProgressWindowCompat {
        private static bool resolved;

        internal static MethodInfo Create { get; private set; }
        internal static MethodInfo Progress { get; private set; }
        internal static MethodInfo RepaintImmediately { get; private set; }

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;
            var windowType = ReflectionUtils.FindType("VF.VRCFProgressWindow");
            Create = ReflectionUtils.FindUniqueMethod(
                windowType, "Create",
                method => method.IsStatic && method.GetParameters().Length == 0);
            Progress = ReflectionUtils.FindMethodWithSignature(
                windowType, "Progress", typeof(void), typeof(float), typeof(string));
            RepaintImmediately = ReflectionUtils.FindNoArgVoid(
                typeof(EditorWindow), "RepaintImmediately");
        }
    }
}
