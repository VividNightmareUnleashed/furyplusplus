using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Un-syncs expression parameters that nothing on the finished avatar reads (no animator
     * condition, blendtree parameter, state slot, or driver-Copy source). The parameter is
     * NEVER deleted — menus, saved state, OSC, and all local behavior keep working; only the
     * remote replication of a value nothing remote consumes is dropped. Bits reclaimed here
     * shrink (or eliminate) VRCFury's parameter-compressor batches downstream.
     *
     * Runs in FppPostBuildHook: after VRCFury's main build, before its compressor hook.
     */
    internal sealed class StripUnusedParamsModule : Module {
        internal static StripUnusedParamsModule Instance { get; private set; }

        internal static readonly ModuleOption KeepDynamicsParams = new ModuleOption(
            "keepDynamicsParams", "Keep PhysBone/Contact parameters synced", false,
            "By default unread dynamics outputs are un-synced too (VRCFury's own guidance). " +
            "Enable if an external system relies on receiving them remotely.");

        /** EditorPrefs (string): semicolon-separated wildcard patterns to never strip, e.g. \"FT/*;OSCm/*\". */
        internal const string KeepListKey = Settings.KeyPrefix + "module.stripUnusedParams.keepList";

        internal StripUnusedParamsModule() {
            Instance = this;
        }

        internal override string Id => "stripUnusedParams";
        internal override string DisplayName => "Strip unused synced parameters";
        internal override ModuleKind Kind => ModuleKind.Pass;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Un-syncs expression parameters that no controller reads — reclaims sync bits " +
            "without touching menus, OSC, or local behavior. Keep-list: " + KeepListKey;

        internal override IReadOnlyList<ModuleOption> Options => new[] { KeepDynamicsParams };

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            // No Harmony patches — this validates the reflection surface the pass needs and
            // fails closed (module off → pass gate false) if VRCFury moved anything.
            StripUnusedParamsPass.Resolve();
        }

        internal override string ReportStats() {
            return StripUnusedParamsPass.LastStats;
        }
    }

    internal static class StripUnusedParamsPass {
        internal static string LastStats;

        private static Type vrcfuryTestType;
        private static MethodInfo isActuallyUploading;

        internal static void Resolve() {
            vrcfuryTestType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFuryTest"),
                "VF.Model.VRCFuryTest"
            );
            isActuallyUploading = ReflectionUtils.Demand(
                ReflectionUtils.FindMethodWithSignature(
                    ReflectionUtils.FindType("VF.Hooks.IsActuallyUploadingHook"), "Get", typeof(bool)
                ),
                "IsActuallyUploadingHook.Get()"
            );
        }

        /** True when the avatar root carries VRCFury's build marker (i.e. VRCFury processed it). */
        internal static bool VrcfuryRanOn(GameObject avatarObject) {
            return vrcfuryTestType != null && avatarObject.GetComponent(vrcfuryTestType) != null;
        }

        /** Returns the stripped parameter names (the hook handles the cross-platform gate). */
        internal static List<string> Run(VRCAvatarDescriptor descriptor, ParamUsageIndex index) {
            var stripped = new List<string>();
            var paramsAsset = descriptor.expressionParameters;
            if (paramsAsset == null || paramsAsset.parameters == null) return stripped;

            var module = StripUnusedParamsModule.Instance;
            var keepDynamics = Settings.IsOptionEnabled(module, StripUnusedParamsModule.KeepDynamicsParams);
            var keepGlobs = ParseKeepList(EditorPrefs.GetString(StripUnusedParamsModule.KeepListKey, ""));
            var keptDynamics = 0;
            var bits = 0;

            foreach (var parameter in paramsAsset.parameters) {
                if (parameter == null || !parameter.networkSynced) continue;
                if (string.IsNullOrEmpty(parameter.name)) continue;
                if (index.Reads.Contains(parameter.name)) continue;
                if (index.DynamicsParams.Contains(parameter.name) && keepDynamics) {
                    keptDynamics++;
                    continue;
                }
                if (keepGlobs.Any(glob => glob.IsMatch(parameter.name))) continue;

                parameter.networkSynced = false;
                stripped.Add(parameter.name);
                bits += VRCExpressionParameters.TypeCost(parameter.valueType);
            }

            if (stripped.Count > 0) {
                EditorUtility.SetDirty(paramsAsset);
                Log.Info($"Stripped sync from {stripped.Count} unused parameter(s), reclaiming {bits} bits: " +
                         string.Join(", ", stripped));
            }
            LastStats = stripped.Count == 0
                ? null
                : $"stripped={stripped.Count} bits={bits}" + (keptDynamics > 0 ? $" keptDynamics={keptDynamics}" : "");

            return stripped;
        }

        /**
         * The combined cross-platform gate for all parameter-mutating passes; called once
         * by the post-build hook. Returns false to hard-fail a diverging mobile build.
         */
        internal static bool HandleCrossPlatform(
            VRCAvatarDescriptor descriptor,
            List<string> stripped,
            List<string> narrowed
        ) {
            var blueprintId = GetBlueprintId(descriptor);
            var target = EditorUserBuildSettings.activeBuildTarget;
            var isMobile = target == BuildTarget.Android || target == BuildTarget.iOS;

            if (isMobile) {
                // Only relevant while VRCFury's own mobile parameter alignment is active.
                if (!EditorPrefs.GetBool("com.vrcfury.alignMobile", true)) return true;
                if (!FppSidecar.VerifyMobileDecision(blueprintId, stripped, out var error, narrowed)) {
                    Log.Error(error);
                    return false;
                }
                return true;
            }

            bool uploading;
            try {
                uploading = (bool)isActuallyUploading.Invoke(null, null);
            } catch {
                uploading = false;
            }
            if (uploading) FppSidecar.SaveDesktopDecision(blueprintId, stripped, narrowed);
            return true;
        }

        private static string GetBlueprintId(VRCAvatarDescriptor descriptor) {
            // PipelineManager lives in a precompiled SDK assembly; reflect to avoid the reference.
            foreach (var component in descriptor.GetComponents<Component>()) {
                if (component == null || component.GetType().Name != "PipelineManager") continue;
                return component.GetType().GetField("blueprintId")?.GetValue(component) as string;
            }
            return null;
        }

        private static List<Regex> ParseKeepList(string raw) {
            var globs = new List<Regex>();
            foreach (var entry in raw.Split(';')) {
                var trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;
                var pattern = "^" + Regex.Escape(trimmed).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                globs.Add(new Regex(pattern, RegexOptions.CultureInvariant));
            }
            return globs;
        }
    }
}
