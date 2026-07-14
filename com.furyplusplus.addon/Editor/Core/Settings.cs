using UnityEditor;

namespace FuryPlusPlus {
    internal static class Settings {
        internal const string KeyPrefix = "com.furyplusplus.";
        internal const string MasterKey = KeyPrefix + "enabled";
        internal const string DetailedProfilingKey = KeyPrefix + "profiling.detailed";

        // Cached so hot phase-boundary checks never hit EditorPrefs; refreshed on every write.
        private static bool? masterCache;

        internal static bool MasterEnabled {
            get {
                masterCache = masterCache ?? EditorPrefs.GetBool(MasterKey, true);
                return masterCache.Value;
            }
            set {
                masterCache = value;
                EditorPrefs.SetBool(MasterKey, value);
            }
        }

        internal static string ModuleKey(Module module) {
            return module.PrefKey;
        }

        internal static string OptionKey(Module module, ModuleOption option) {
            return option.KeyFor(module);
        }

        internal static bool IsModuleEnabled(Module module) {
            if (!MasterEnabled) return false;
            if (BenchmarkForcesOff(module)) return false;
            return EditorPrefs.GetBool(ModuleKey(module), module.DefaultEnabled);
        }

        /**
         * While a stock benchmark is armed, every module except the profiler reads as
         * disabled, so the next bake measures stock VRCFury with the same measurement
         * overhead as a normal bake. Only bites while an installed, enabled profiler can
         * actually complete the benchmark (and clear the flag at bake end) — otherwise
         * the flag is inert instead of silently disabling every future bake.
         */
        private static bool BenchmarkForcesOff(Module module) {
            if (module is ProfilingModule) return false;
            if (!BakeHistory.BenchmarkPending) return false;
            var profiling = ProfilingModule.Instance;
            return profiling != null && ModuleRegistry.IsActive(profiling)
                   && EditorPrefs.GetBool(ModuleKey(profiling), profiling.DefaultEnabled);
        }

        internal static void SetModuleEnabled(Module module, bool value) {
            EditorPrefs.SetBool(ModuleKey(module), value);
        }

        internal static bool IsOptionEnabled(Module module, ModuleOption option) {
            return EditorPrefs.GetBool(OptionKey(module, option), option.Default);
        }

        internal static void SetOptionEnabled(Module module, ModuleOption option, bool value) {
            EditorPrefs.SetBool(OptionKey(module, option), value);
        }

        internal static string GetListOption(Module module, ModuleListOption option) {
            return EditorPrefs.GetString(option.KeyFor(module), "");
        }

        internal static void SetListOption(Module module, ModuleListOption option, string value) {
            EditorPrefs.SetString(option.KeyFor(module), value ?? "");
        }

        internal static bool DetailedProfiling {
            get { return EditorPrefs.GetBool(DetailedProfilingKey, false); }
            set { EditorPrefs.SetBool(DetailedProfilingKey, value); }
        }

        /** Delete every module/option override so defaults (the recommended values) apply again. */
        internal static void RestoreRecommended() {
            MasterEnabled = true;
            foreach (var module in ModuleRegistry.All) {
                EditorPrefs.DeleteKey(ModuleKey(module));
                foreach (var option in module.Options) {
                    EditorPrefs.DeleteKey(OptionKey(module, option));
                }
                foreach (var option in module.ListOptions) {
                    EditorPrefs.DeleteKey(option.KeyFor(module));
                }
            }
            EditorPrefs.DeleteKey(DetailedProfilingKey);
            BakeHistory.BenchmarkPending = false;
        }

        /** Panic switch: disable every module (master switch stays, for the nuclear option). */
        internal static void DisableAllModules() {
            foreach (var module in ModuleRegistry.All) {
                SetModuleEnabled(module, false);
            }
        }
    }
}
