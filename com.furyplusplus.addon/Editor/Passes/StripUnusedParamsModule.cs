using System;
using System.Collections.Generic;
using System.Linq;
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
    internal sealed class StripUnusedParamsModule : Module<StripUnusedParamsModule> {
        internal static readonly ModuleOption KeepDynamicsParams = new ModuleOption(
            "keepDynamicsParams", "Keep PhysBone/Contact parameters synced", false,
            "By default unread dynamics outputs are un-synced too (VRCFury's own guidance). " +
            "Enable if an external system relies on receiving them remotely.");

        /** Shared with the Int-narrowing pass: parameters matching these globs are never touched. */
        internal static readonly ModuleListOption KeepList = new ModuleListOption(
            "keepList", "Keep-list (never strip/narrow)",
            "Semicolon-separated wildcard patterns of parameters to never strip or narrow, " +
            "e.g. \"FT/*;OSCm/*\".");

        private static readonly ModuleOption[] AllOptions = { KeepDynamicsParams };
        private static readonly ModuleListOption[] AllListOptions = { KeepList };

        internal override string Id => "stripUnusedParams";
        internal override string DisplayName => "Strip unused synced parameters";
        internal override ModuleKind Kind => ModuleKind.Pass;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Synced parameters";
        internal override string Description =>
            "Un-syncs expression parameters that no controller reads — reclaims sync bits " +
            "without touching menus, OSC, or local behavior.";

        internal override IReadOnlyList<ModuleOption> Options => AllOptions;
        internal override IReadOnlyList<ModuleListOption> ListOptions => AllListOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            // No Harmony patches — this validates the reflection surface the pass needs and
            // fails closed (module off → pass gate false) if VRCFury moved anything.
            StripUnusedParamsPass.Resolve();
        }

        internal override string ReportStats() {
            return StripUnusedParamsPass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            if (analysis?.StrippableBits > 0) {
                return ($"-{analysis.Value.StrippableBits} sync bits",
                    $"{analysis.Value.StrippableParams} unused synced parameter(s) on the analyzed avatar.");
            }
            return StripUnusedParamsPass.LastBits > 0
                ? ($"-{StripUnusedParamsPass.LastBits} sync bits last bake", StripUnusedParamsPass.LastStats)
                : ((string, string)?)null;
        }

        /** The shared keep-list as compiled globs, with the user's current setting. */
        internal static List<Regex> CurrentKeepGlobs() {
            return Globs.Parse(Settings.GetListOption(Instance, KeepList));
        }
    }

    internal static class StripUnusedParamsPass {
        internal static string LastStats;
        internal static int LastBits;

        private static Type vrcfuryTestType;

        internal enum KeepReason {
            /** Strippable — no keep reason applies. */
            None,
            /** Not a synced named parameter at all. */
            NotEligible,
            Read,
            Dynamics,
            KeepList
        }

        internal static void Resolve() {
            vrcfuryTestType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFuryTest"),
                "VF.Model.VRCFuryTest"
            );
            UploadCompat.DemandCore();
        }

        /** True when the avatar root carries VRCFury's build marker (i.e. VRCFury processed it). */
        internal static bool VrcfuryRanOn(GameObject avatarObject) {
            return vrcfuryTestType != null && avatarObject.GetComponent(vrcfuryTestType) != null;
        }

        /**
         * The one strippability doctrine — Run applies it to the built avatar and
         * Estimators.Analyze projects it onto the unbaked one, so the settings-window
         * numbers can never drift from what the pass actually does.
         */
        internal static KeepReason Classify(
            VRCExpressionParameters.Parameter parameter,
            ParamUsageIndex index,
            bool keepDynamics,
            List<Regex> keepGlobs
        ) {
            if (parameter == null || !parameter.networkSynced) return KeepReason.NotEligible;
            if (string.IsNullOrEmpty(parameter.name)) return KeepReason.NotEligible;
            if (index.Reads.Contains(parameter.name)) return KeepReason.Read;
            if (keepDynamics && index.DynamicsParams.Contains(parameter.name)) return KeepReason.Dynamics;
            if (keepGlobs.Any(glob => glob.IsMatch(parameter.name))) return KeepReason.KeepList;
            return KeepReason.None;
        }

        /** Returns the stripped parameter names (the hook handles the cross-platform gate). */
        internal static List<string> Run(VRCAvatarDescriptor descriptor, ParamUsageIndex index) {
            var stripped = new List<string>();
            var paramsAsset = descriptor.expressionParameters;
            if (paramsAsset == null || paramsAsset.parameters == null) return stripped;

            var module = StripUnusedParamsModule.Instance;
            var keepDynamics = Settings.IsOptionEnabled(module, StripUnusedParamsModule.KeepDynamicsParams);
            var keepGlobs = StripUnusedParamsModule.CurrentKeepGlobs();
            var keptDynamics = 0;
            var bits = 0;

            foreach (var parameter in paramsAsset.parameters) {
                var reason = Classify(parameter, index, keepDynamics, keepGlobs);
                if (reason == KeepReason.Dynamics) keptDynamics++;
                if (reason != KeepReason.None) continue;

                parameter.networkSynced = false;
                stripped.Add(parameter.name);
                bits += VRCExpressionParameters.TypeCost(parameter.valueType);
            }

            if (stripped.Count > 0) {
                EditorUtility.SetDirty(paramsAsset);
                Log.Info($"Stripped sync from {stripped.Count} unused parameter(s), reclaiming {bits} bits: " +
                         string.Join(", ", stripped));
            }
            LastBits = stripped.Count == 0 ? 0 : bits;
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

            // Failure default: assume NOT uploading, so a broken gate never records a
            // desktop decision for a build that may not ship.
            if (UploadCompat.IsActuallyUploading(assumeOnFailure: false)) {
                FppSidecar.SaveDesktopDecision(blueprintId, stripped, narrowed);
            }
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
    }
}
