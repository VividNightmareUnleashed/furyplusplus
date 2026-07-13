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

        internal static Module Find(string id) {
            return All.FirstOrDefault(module => module.Id == id);
        }

        internal static IEnumerable<Module> ByKind(ModuleKind kind) {
            return All.Where(module => module.Kind == kind);
        }

        /** Compact per-module state summary for the profiler report footer. */
        internal static string DescribeStates() {
            var builder = new StringBuilder();
            foreach (var module in All) {
                if (builder.Length > 0) builder.Append(", ");
                var status = GetStatus(module);
                var suffix = status.State == ModuleState.Installed
                    ? (module.Enabled ? "on" : "off")
                    : status.State.ToString();
                builder.Append(module.Id).Append('=').Append(suffix);
            }
            return builder.Length > 0 ? builder.ToString() : "(no modules)";
        }

        private static void Set(Module module, ModuleState state, string message) {
            Statuses[module.Id] = (state, message);
        }
    }
}
