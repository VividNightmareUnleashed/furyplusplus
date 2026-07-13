using UnityEditor;

namespace FuryPlusPlus {
    internal static class Settings {
        internal const string KeyPrefix = "com.furyplusplus.";
        internal const string MasterKey = KeyPrefix + "enabled";
        internal const string DetailedProfilingKey = KeyPrefix + "profiling.detailed";
        internal const string WelcomeShownVersionKey = KeyPrefix + "welcomeShownVersion";

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
            return KeyPrefix + "module." + module.Id;
        }

        internal static string OptionKey(Module module, ModuleOption option) {
            return ModuleKey(module) + "." + option.Suffix;
        }

        internal static bool IsModuleEnabled(Module module) {
            return MasterEnabled && EditorPrefs.GetBool(ModuleKey(module), module.DefaultEnabled);
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
            }
            EditorPrefs.DeleteKey(DetailedProfilingKey);
        }

        /** Panic switch: disable every module (master switch stays, for the nuclear option). */
        internal static void DisableAllModules() {
            foreach (var module in ModuleRegistry.All) {
                SetModuleEnabled(module, false);
            }
        }
    }
}
