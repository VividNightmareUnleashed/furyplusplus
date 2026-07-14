using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;

namespace FuryPlusPlus {
    internal enum ModuleState {
        /** Bootstrap has not (successfully) run; nothing is patched. */
        NotInstalled,
        Installed,
        /** VRCFury version/tier requirement not met; module never attempted. */
        DisabledIncompatible,
        /** Install() threw; module is off for this domain load. */
        Failed
    }

    internal static class ModuleRegistry {
        /**
         * Explicit, ordered — install order is semantic (shared compat areas resolve before
         * dependents; multiple prefixes on one method run in patch order). Modules are appended
         * here as they land; never auto-discover.
         */
        internal static readonly Module[] All = {
            new ProfilingModule(),
            // --- Speed (ported from QuickFury; QuickFury's exact install order) ---
            new OrderedPathRewriteModule(),
            new ArmatureConstraintIndexModule(),
            new ArmaturePhysboneIndexModule(),
            new ArmatureSkinIndexModule(),
            new ArmatureDestroyIndexModule(),
            new ArmatureDebugInfoModule(),
            new FastArmatureMoveModule(),
            new SaveAssetsDiscoveryModule(),
            new SaveAssetsBatchingModule(),
            new ConsolidatedAssetContainerModule(),
            new FastControllerAssetGraphModule(),
            new BlendshapeBindingCacheModule(),
            new SpsCoveredRendererModule(),
            new SpsMaterialProbeCacheModule(),
            new ControllerParameterIndexModule(),
            new LayerToTreeLayerIndexModule(),
            new TrackingBehaviourIndexModule(),
            new BehaviourContainerFilterModule(),
            // --- New FuryPlusPlus speed modules ---
            new PlayModeSkipsModule(),
            new ProgressWindowThemeModule(),
            new LayerToTreeBindingIndexModule(),
            new CompressorMemoModule(),
            new GetLayersMemoModule(),
            new MergePathCacheModule(),
            new AnimatorIteratorMemoModule(),
            new BlendshapeBakeRewriteModule(),
            // --- Quality passes (post-build hook order) ---
            new StripUnusedParamsModule(),
            new FullScopeDbtModule(),
            new NoOpCurveStripModule(),
            new ClipDedupModule(),
            new OffSideEliminationModule(),
            // Toggle conversions fire before DBT consolidation (hook order = install order)
            // so their created blendtree layers are consolidation candidates.
            new ToggleSeparateLocalModule(),
            new ToggleFadeModule(),
            new DbtConsolidationModule(),
            new NarrowIntParamsModule(),
            // --- Compressor family; first Install wins the shared scope patch ---
            new CompressorLanePackingModule(),
            new CompressorSolverModule(),
            new CompressorEligibilityModule(),
            new CompressorSub8Module(),
            // --- Experimental play-mode iteration; all default OFF ---
            new PlayModeNoDiskSaveModule(),
            // Both bake-cache modules are independent participants of the shared
            // BakeChainAnchor; install order = dispatch order (telemetry logs its verdict
            // before replay can skip the chain).
            new BakeCacheDryRunModule(),
            new BakeCacheReplayModule(),
            // --- UI: keeps the liquid progress bar animating inside long phases ---
            new ProgressPumpModule(),
        };

        private static readonly Dictionary<string, (ModuleState State, string Message)> Statuses =
            new Dictionary<string, (ModuleState, string)>(StringComparer.Ordinal);

        internal static void InstallAll(Harmony harmony, VrcfuryCompat compat) {
            Statuses.Clear();
            var installed = 0;
            foreach (var module in All) {
                if (!compat.Satisfies(module.RequiredTier)) {
                    Set(module, ModuleState.DisabledIncompatible,
                        $"requires {module.RequiredTier} (VRCFury {compat.PackageVersion}, tested {VrcfuryCompat.PinnedVersion})");
                    continue;
                }
                try {
                    module.Install(harmony, compat);
                    Set(module, ModuleState.Installed, null);
                    installed++;
                } catch (Exception e) {
                    Set(module, ModuleState.Failed, e.Message);
                    Log.Warn($"{module.DisplayName} disabled: {e.Message}");
                }
            }
            Log.Info(
                $"Ready: {installed}/{All.Length} modules installed for VRCFury {compat.PackageVersion} " +
                $"(MVID {compat.ModuleVersionId})."
            );
        }

        internal static (ModuleState State, string Message) GetStatus(Module module) {
            return Statuses.TryGetValue(module.Id, out var status)
                ? status
                : (ModuleState.NotInstalled, null);
        }

        internal static bool IsActive(Module module) {
            return GetStatus(module).State == ModuleState.Installed;
        }

        /** Null-safe "installed for this domain load AND switched on". */
        internal static bool IsOn(Module module) {
            return module != null && IsActive(module) && module.Enabled;
        }

        internal static Module Find(string id) {
            return All.FirstOrDefault(module => module.Id == id);
        }

        internal static IEnumerable<Module> ByKind(ModuleKind kind) {
            return All.Where(module => module.Kind == kind);
        }

        /**
         * Compact per-module state summary for the profiler report footer. Sub-option states
         * ride along as id=on[opt1=on,opt2=off]. Human report line only — the bake-cache
         * config hash reads DescribeOutputConfig instead.
         */
        internal static string DescribeStates() {
            var builder = new StringBuilder();
            foreach (var module in All) {
                if (builder.Length > 0) builder.Append(", ");
                var status = GetStatus(module);
                var suffix = status.State == ModuleState.Installed
                    ? (module.Enabled ? "on" : "off")
                    : status.State.ToString();
                builder.Append(module.Id).Append('=').Append(suffix);
                for (var i = 0; i < module.Options.Count; i++) {
                    builder.Append(i == 0 ? '[' : ',').Append(module.Options[i].Suffix).Append('=')
                        .Append(Settings.IsOptionEnabled(module, module.Options[i]) ? "on" : "off");
                }
                if (module.Options.Count > 0) builder.Append(']');
            }
            return builder.Length > 0 ? builder.ToString() : "(no modules)";
        }

        /**
         * Bake-output-relevant config only — the bake-cache config-hash input. Quality/Pass
         * modules contribute their effective state plus every option; Speed/Cosmetic modules
         * contribute only when they declare an output-affecting option (bug-fix sub-toggles,
         * play-mode skips) or a list setting, so a cosmetic or pure-speed toggle never
         * invalidates a snapshot. List settings ride along normalized.
         */
        internal static string DescribeOutputConfig() {
            var builder = new StringBuilder();
            foreach (var module in All) {
                var alwaysRelevant = module.Kind == ModuleKind.Quality || module.Kind == ModuleKind.Pass;
                var relevant = alwaysRelevant;
                if (!relevant) {
                    foreach (var option in module.Options) {
                        if (option.AffectsBakeOutput) { relevant = true; break; }
                    }
                    if (module.ListOptions.Count > 0) relevant = true;
                }
                if (!relevant) continue;

                var on = IsOn(module);
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(module.Id).Append('=').Append(on ? "on" : "off");
                if (!on) continue; // a disabled module's options can't affect output

                var first = true;
                foreach (var option in module.Options) {
                    if (!alwaysRelevant && !option.AffectsBakeOutput) continue;
                    builder.Append(first ? '[' : ',').Append(option.Suffix).Append('=')
                        .Append(Settings.IsOptionEnabled(module, option) ? "on" : "off");
                    first = false;
                }
                foreach (var option in module.ListOptions) {
                    builder.Append(first ? '[' : ',').Append(option.Suffix).Append('=')
                        .Append('\'').Append(Globs.Normalize(Settings.GetListOption(module, option)))
                        .Append('\'');
                    first = false;
                }
                if (!first) builder.Append(']');
            }
            return builder.Length > 0 ? builder.ToString() : "(no modules)";
        }

        private static void Set(Module module, ModuleState state, string message) {
            Statuses[module.Id] = (state, message);
        }
    }
}
