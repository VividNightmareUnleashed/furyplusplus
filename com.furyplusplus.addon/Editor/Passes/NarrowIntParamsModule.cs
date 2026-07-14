using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Narrows synced Int expression parameters whose entire observable usage is 0/1 down to
     * Bool — 7 sync bits reclaimed each. ONLY the expression parameter changes: VRCFury's
     * UpgradeWrongParamTypes is animator-side-only and VRChat natively casts an expression
     * Bool into Int/Float animator parameters, so animators are left untouched.
     *
     * Closed-world eligibility — ANY unrecognized usage shape disqualifies:
     *  - every menu control referencing it is Toggle/Button/SubMenu with value == 1
     *    (puppet usage disqualifies);
     *  - every animator condition ∈ {Equals 0/1, NotEqual 0/1, Greater 0, Less 1};
     *  - drivers only Set 0/1 (Add/Random/Copy-destination disqualify);
     *  - never an AAP write target; default value ∈ {0, 1}; not on the keep-list;
     *  - OSC-suspect params (reads but no menu control and no driver write) are skipped
     *    and reported — an external OSC app may send typed Int messages.
     * Runs after the unused-param strip in the post-build hook; PC↔Quest agreement is
     * enforced by the sidecar (mobile builds hard-fail on divergence).
     */
    internal sealed class NarrowIntParamsModule : Module<NarrowIntParamsModule> {
        internal override string Id => "narrowIntParams";
        internal override string DisplayName => "Narrow 0/1 Int parameters to Bool";
        internal override ModuleKind Kind => ModuleKind.Pass;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string SettingsGroup => "Synced parameters";
        internal override string Description =>
            "Synced Ints that only ever hold 0/1 become Bools (7 bits saved each). " +
            "Only the expression parameter changes; VRChat casts it into the animator. " +
            "Shares the keep-list of \"Strip unused synced parameters\".";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            // Shares StripUnusedParamsPass's resolved surface; validate it resolves.
            StripUnusedParamsPass.Resolve();
        }

        internal override string ReportStats() {
            return NarrowIntParamsPass.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            if (analysis?.NarrowableInts > 0) {
                return ($"-{analysis.Value.NarrowableInts * 7} sync bits",
                    $"{analysis.Value.NarrowableInts} 0/1 Int parameter(s) narrowable to Bool (7 bits each).");
            }
            return NarrowIntParamsPass.LastBits > 0
                ? ($"-{NarrowIntParamsPass.LastBits} sync bits last bake", NarrowIntParamsPass.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class NarrowIntParamsPass {
        internal static string LastStats;
        internal static int LastBits;

        internal enum Verdict {
            Eligible,
            Ineligible,
            /** Reads exist but no menu/driver writes — possibly OSC-driven typed input. */
            OscSuspect
        }

        /**
         * The one narrowing doctrine — Run applies it to the built avatar and
         * Estimators.Analyze projects it onto the unbaked one, so the settings-window
         * numbers can never drift from what the pass actually does.
         */
        internal static Verdict Classify(
            VRCExpressionParameters.Parameter parameter,
            ParamUsageIndex index,
            List<Regex> keepGlobs
        ) {
            if (parameter == null || !parameter.networkSynced) return Verdict.Ineligible;
            if (parameter.valueType != VRCExpressionParameters.ValueType.Int) return Verdict.Ineligible;
            if (string.IsNullOrEmpty(parameter.name)) return Verdict.Ineligible;
            if (parameter.defaultValue != 0 && parameter.defaultValue != 1) return Verdict.Ineligible;
            if (keepGlobs.Any(glob => glob.IsMatch(parameter.name))) return Verdict.Ineligible;

            index.Details.TryGetValue(parameter.name, out var detail);
            var isRead = index.Reads.Contains(parameter.name);
            var hasMenu = detail?.HasMenuControl == true;
            var hasDriverWrite = detail?.HasDriverWrite == true;

            if (isRead && !hasMenu && !hasDriverWrite) {
                // Narrowing would change the wire type an external OSC app may rely on.
                return Verdict.OscSuspect;
            }
            if (detail != null) {
                if (detail.UsedAsPuppet) return Verdict.Ineligible;
                if (detail.MenuValueOtherThanOne) return Verdict.Ineligible;
                if (detail.HasUnsupportedCondition) return Verdict.Ineligible;
                if (detail.DriverNonBinaryWrite) return Verdict.Ineligible;
                if (detail.AapTarget) return Verdict.Ineligible;
            }
            return Verdict.Eligible;
        }

        internal static List<string> Run(VRCAvatarDescriptor descriptor, ParamUsageIndex index) {
            var paramsAsset = descriptor.expressionParameters;
            var narrowed = new List<string>();
            var oscSuspects = new List<string>();
            if (paramsAsset == null || paramsAsset.parameters == null) return narrowed;

            var keepGlobs = StripUnusedParamsModule.CurrentKeepGlobs();

            foreach (var parameter in paramsAsset.parameters) {
                switch (Classify(parameter, index, keepGlobs)) {
                    case Verdict.OscSuspect:
                        oscSuspects.Add(parameter.name);
                        continue;
                    case Verdict.Ineligible:
                        continue;
                }

                parameter.valueType = VRCExpressionParameters.ValueType.Bool;
                parameter.defaultValue = parameter.defaultValue != 0 ? 1 : 0;
                narrowed.Add(parameter.name);
            }

            if (narrowed.Count > 0) {
                EditorUtility.SetDirty(paramsAsset);
                Log.Info($"Narrowed {narrowed.Count} Int parameter(s) to Bool, reclaiming " +
                         $"{narrowed.Count * 7} bits: {string.Join(", ", narrowed)}");
            }
            if (oscSuspects.Count > 0) {
                Log.Info("Skipped possible OSC-driven Int parameter(s) (reads but no menu/driver " +
                         "writes): " + string.Join(", ", oscSuspects) +
                         " — add to the keep-list to silence this.");
            }
            LastBits = narrowed.Count * 7;
            LastStats = narrowed.Count == 0 ? null : $"narrowed={narrowed.Count} bits={narrowed.Count * 7}";
            return narrowed;
        }
    }
}
