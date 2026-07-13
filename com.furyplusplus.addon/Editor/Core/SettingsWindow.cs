using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * The dedicated FuryPlusPlus window (Tools/FuryPlusPlus/Settings…). Renders defensively
     * from ModuleRegistry.GetStatus — it can be opened before the delayCall bootstrap ran,
     * or with VRCFury absent.
     */
    internal class SettingsWindow : EditorWindow {
        private readonly Dictionary<ModuleKind, bool> foldouts = new Dictionary<ModuleKind, bool> {
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

        private Vector2 scroll;
        private VRC.SDK3.Avatars.Components.VRCAvatarDescriptor estimateTarget;
        private Estimators.Result? estimate;

        internal static void Open() {
            var window = GetWindow<SettingsWindow>(utility: false, title: "FuryPlusPlus", focus: true);
            window.minSize = new Vector2(440, 380);
            window.Show();
        }

        private void OnGUI() {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space();
            DrawStatusBanner();
            EditorGUILayout.Space();
            DrawEstimator();
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
                    foldouts[kind] = EditorGUILayout.BeginFoldoutHeaderGroup(foldouts[kind], title);
                    if (foldouts[kind]) {
                        foreach (var module in modules) DrawModule(module);
                    }
                    EditorGUILayout.EndFoldoutHeaderGroup();
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Profiling", EditorStyles.boldLabel);
                var detailed = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Detailed profiling",
                        "Times VRCFury's hottest internals per bake. Installs the extra patches " +
                        "immediately; turning it off sheds them on the next script reload."
                    ),
                    Settings.DetailedProfiling
                );
                if (detailed != Settings.DetailedProfiling) {
                    Settings.DetailedProfiling = detailed;
                    if (detailed) ProfilePatches.EnsureDetailedTargetsInstalled();
                }
                if (GUILayout.Button("Log last profile report", GUILayout.Width(180))) {
                    var report = FuryPlusPlusProfilerApi.LastReport;
                    if (string.IsNullOrEmpty(report)) {
                        report = SessionState.GetString("FuryPlusPlus.LastProfile", "");
                    }
                    if (string.IsNullOrEmpty(report)) {
                        Log.Info("No profile report captured yet — run a VRCFury bake first.");
                    } else {
                        Debug.Log(report);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawEstimator() {
            EditorGUILayout.LabelField("Estimated savings", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope()) {
                estimateTarget = (VRC.SDK3.Avatars.Components.VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                    estimateTarget, typeof(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor), true);
                using (new EditorGUI.DisabledScope(estimateTarget == null)) {
                    if (GUILayout.Button("Analyze", GUILayout.Width(80))) {
                        estimate = Estimators.Analyze(estimateTarget);
                    }
                }
            }
            if (estimate.HasValue) {
                var result = estimate.Value;
                string Fmt(int value, string text) => value < 0 ? "n/a" : string.Format(text, value);
                EditorGUILayout.HelpBox(
                    $"Synced parameter bits: {Fmt(result.SyncedBits, "{0}")} / {result.MaxBits}\n" +
                    $"Un-syncable unused parameters: {Fmt(result.StrippableParams, "{0}")}" +
                    (result.StrippableBits > 0 ? $" (~{result.StrippableBits} bits)" : "") + "\n" +
                    $"Ints narrowable to Bool: {Fmt(result.NarrowableInts, "{0}")}" +
                    (result.NarrowableInts > 0 ? $" (~{result.NarrowableInts * 7} bits)" : "") + "\n" +
                    $"FX layers (pre-bake): {Fmt(result.FxLayers, "{0}")}\n" +
                    $"Non-animated blendshapes: {Fmt(result.NonAnimatedBlendshapes, "{0}")}" +
                    (result.BlendshapeMeshes > 0 ? $" across {result.BlendshapeMeshes} mesh(es)" : "") +
                    "\n\nProjections against the un-baked avatar — the baked result differs " +
                    "(VRCFury adds parameters/layers). Speed savings are measured per bake; " +
                    "see the profiler report after your next build.",
                    MessageType.None
                );
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
