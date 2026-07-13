using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Preferences/FuryPlusPlus page. Renders defensively from ModuleRegistry.GetStatus —
     * it can be opened before the delayCall bootstrap ran, or with VRCFury absent.
     */
    internal static class FppSettingsProvider {
        private static readonly Dictionary<ModuleKind, bool> Foldouts = new Dictionary<ModuleKind, bool> {
            { ModuleKind.Speed, true },
            { ModuleKind.Quality, true },
            { ModuleKind.Pass, true },
            { ModuleKind.Cosmetic, true },
        };

        private static readonly (ModuleKind Kind, string Title)[] Groups = {
            (ModuleKind.Speed, "Build speed (output-identical)"),
            (ModuleKind.Quality, "Output quality (changes bake output)"),
            (ModuleKind.Pass, "Standalone passes"),
            (ModuleKind.Cosmetic, "Extras"),
        };

        [SettingsProvider]
        public static SettingsProvider Create() {
            return new SettingsProvider("Preferences/FuryPlusPlus", SettingsScope.User) {
                guiHandler = _ => Draw(),
                keywords = new HashSet<string> { "VRCFury", "FuryPlusPlus", "optimization", "bake", "avatar" }
            };
        }

        private static void Draw() {
            EditorGUILayout.Space();
            DrawStatusBanner();
            EditorGUILayout.Space();

            var master = EditorGUILayout.ToggleLeft(
                new GUIContent("Enable FuryPlusPlus", "Master switch for every module."),
                Settings.MasterEnabled
            );
            if (master != Settings.MasterEnabled) {
                Settings.MasterEnabled = master;
                if (master && Bootstrap.Compat == null) {
                    // Turned back on mid-session after boot skipped installing — install now.
                    Bootstrap.Initialize();
                }
            }

            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("Restore recommended", GUILayout.Width(160))) {
                    Settings.RestoreRecommended();
                    if (Bootstrap.Compat == null) Bootstrap.Initialize();
                }
                if (GUILayout.Button("Disable all optimizations", GUILayout.Width(180))) {
                    Settings.DisableAllModules();
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(!Settings.MasterEnabled)) {
                foreach (var (kind, title) in Groups) {
                    var modules = ModuleRegistry.ByKind(kind).ToList();
                    if (modules.Count == 0) continue;
                    Foldouts[kind] = EditorGUILayout.BeginFoldoutHeaderGroup(Foldouts[kind], title);
                    if (Foldouts[kind]) {
                        foreach (var module in modules) DrawModule(module);
                    }
                    EditorGUILayout.EndFoldoutHeaderGroup();
                }
            }
        }

        private static void DrawStatusBanner() {
            var compat = Bootstrap.Compat;
            if (compat != null) {
                if (compat.IsExactVersion) {
                    EditorGUILayout.HelpBox(
                        $"Full compatibility: VRCFury {compat.PackageVersion}.",
                        MessageType.Info
                    );
                } else {
                    EditorGUILayout.HelpBox(
                        $"VRCFury {compat.PackageVersion} detected — tested with {VrcfuryCompat.PinnedVersion}. " +
                        "Version-pinned modules are disabled (profiling and SDK-level passes stay active).",
                        MessageType.Warning
                    );
                }
            } else if (Bootstrap.DisabledReason != null) {
                EditorGUILayout.HelpBox("FuryPlusPlus inactive: " + Bootstrap.DisabledReason, MessageType.Warning);
            } else {
                EditorGUILayout.HelpBox("FuryPlusPlus has not initialized yet.", MessageType.None);
            }
        }

        private static void DrawModule(Module module) {
            var status = ModuleRegistry.GetStatus(module);
            var installed = status.State == ModuleState.Installed;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!installed)) {
                    var enabled = EditorPrefs.GetBool(Settings.ModuleKey(module), module.DefaultEnabled);
                    var toggled = EditorGUILayout.ToggleLeft(
                        new GUIContent(module.DisplayName, module.Description),
                        enabled
                    );
                    if (toggled != enabled) Settings.SetModuleEnabled(module, toggled);
                }
                if (!installed) {
                    var label = status.State == ModuleState.NotInstalled
                        ? "not installed"
                        : status.State == ModuleState.DisabledIncompatible
                            ? "incompatible: " + status.Message
                            : "failed: " + status.Message;
                    EditorGUILayout.LabelField("— " + label, EditorStyles.miniLabel);
                }
            }

            if (module.Options.Count > 0) {
                using (new EditorGUI.IndentLevelScope()) {
                    using (new EditorGUI.DisabledScope(!installed)) {
                        foreach (var option in module.Options) {
                            var value = Settings.IsOptionEnabled(module, option);
                            var toggled = EditorGUILayout.ToggleLeft(
                                new GUIContent(option.Label, option.Description),
                                value
                            );
                            if (toggled != value) Settings.SetOptionEnabled(module, option, toggled);
                        }
                    }
                }
            }
        }
    }
}
