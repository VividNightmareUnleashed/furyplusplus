using System;
using System.Linq;
using System.Reflection;
using UnityEditor.PackageManager;

namespace FuryPlusPlus {
    /**
     * The per-domain-load VRCFury compatibility gate. Holds the always-resolved profiling
     * members (usable on any VRCFury version) and answers tier checks. Per-subsystem member
     * lookups live in lazy area holders (ArmatureCompat, ...), resolved on first module use.
     */
    internal sealed class VrcfuryCompat {
        internal const string PinnedVersion = "1.1367.0";
        internal const string AvatarsEditorAssemblyName = "VRCFury-Editor-Avatars";

        internal string PackageVersion { get; private set; }
        internal Guid ModuleVersionId { get; private set; }
        internal Assembly AvatarsEditorAssembly { get; private set; }

        // Profiling tier members — resolvable on any supported VRCFury version.
        internal MethodInfo RunMain { get; private set; }
        internal MethodInfo ActionCall { get; private set; }
        internal MethodInfo ActionGetName { get; private set; }
        internal MethodInfo ActionGetService { get; private set; }

        internal bool IsExactVersion => PackageVersion == PinnedVersion;

        internal bool Satisfies(CompatTier tier) {
            switch (tier) {
                case CompatTier.Profiling:
                    return true; // TryCreate fails outright when profiling members are missing.
                case CompatTier.PublicSdk:
                    return true;
                case CompatTier.ExactVersion:
                    return IsExactVersion;
                default:
                    return false;
            }
        }

        internal static Assembly FindAvatarsAssembly() {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == AvatarsEditorAssemblyName);
        }

        /** Version of the installed VRCFury package, or null when it is not loaded at all. */
        internal static string LoadedPackageVersion() {
            var avatarsAssembly = FindAvatarsAssembly();
            if (avatarsAssembly == null) return null;
            return PackageInfo.FindForAssembly(avatarsAssembly)?.version ?? "unknown";
        }

        internal static bool TryCreate(out VrcfuryCompat compat, out string error) {
            compat = null;
            error = null;

            try {
                var output = new VrcfuryCompat();
                var avatarsAssembly = FindAvatarsAssembly();
                if (avatarsAssembly == null) {
                    error = AvatarsEditorAssemblyName + " is not loaded";
                    return false;
                }

                output.AvatarsEditorAssembly = avatarsAssembly;
                output.PackageVersion = PackageInfo.FindForAssembly(avatarsAssembly)?.version ?? "unknown";
                output.ModuleVersionId = avatarsAssembly.ManifestModule.ModuleVersionId;

                var builderType = avatarsAssembly.GetType("VF.Builder.VRCFuryBuilder", false);
                output.RunMain = ReflectionUtils.FindUniqueMethod(
                    builderType,
                    "RunMain",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.ReturnType == typeof(void) && method.GetParameters().Length == 1
                );

                var actionType = avatarsAssembly.GetType("VF.Feature.Base.FeatureBuilderAction", false);
                output.ActionCall = ReflectionUtils.FindUniqueMethod(
                    actionType,
                    "Call",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.ReturnType == typeof(void) && method.GetParameters().Length == 0
                );
                output.ActionGetName = ReflectionUtils.FindUniqueMethod(
                    actionType,
                    "GetName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.ReturnType == typeof(string) && method.GetParameters().Length == 0
                );
                output.ActionGetService = ReflectionUtils.FindUniqueMethod(
                    actionType,
                    "GetService",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.ReturnType == typeof(object) && method.GetParameters().Length == 0
                );

                if (output.RunMain == null || output.ActionCall == null
                                           || output.ActionGetName == null || output.ActionGetService == null) {
                    error = "VRCFury profiling targets did not match their expected signatures";
                    return false;
                }

                compat = output;
                return true;
            } catch (Exception e) {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }
}
