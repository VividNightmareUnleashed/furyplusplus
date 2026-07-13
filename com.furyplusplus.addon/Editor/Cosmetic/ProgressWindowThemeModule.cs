using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuryPlusPlus {
    /**
     * Additive theming of VRCFury's build progress window: accent border and a
     * "FuryPlusPlus — N modules active" badge, replaced by a prominent warning banner when
     * running on an untested VRCFury version or with the master switch off — degraded mode
     * becomes visible at every bake instead of only in the console.
     *
     * Deliberately Profiling-tier, NOT ExactVersion: the window must still be patchable on
     * unknown VRCFury versions precisely so it can DISPLAY the version-mismatch warning.
     * Fails closed only if Create/Progress themselves no longer resolve; any styling
     * exception falls open to the stock look.
     */
    internal sealed class ProgressWindowThemeModule : Module {
        internal static ProgressWindowThemeModule Instance { get; private set; }

        internal static readonly ModuleOption LiveTimings = new ModuleOption(
            "liveTimings", "Show live phase timings", true,
            "Appends the currently-running VRCFury phase and its elapsed time to the badge.");

        internal ProgressWindowThemeModule() {
            Instance = this;
        }

        internal override string Id => "progressWindowTheme";
        internal override string DisplayName => "Progress window theme";
        internal override ModuleKind Kind => ModuleKind.Cosmetic;
        internal override CompatTier RequiredTier => CompatTier.Profiling;
        internal override string Description =>
            "Themes VRCFury's build progress window and surfaces FuryPlusPlus status " +
            "(including a warning when optimizations are disabled by a version mismatch).";

        internal override System.Collections.Generic.IReadOnlyList<ModuleOption> Options =>
            new[] { LiveTimings };

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ProgressWindowThemePatch.Install(harmony, compat);
        }
    }

    internal static class ProgressWindowThemePatch {
        private static readonly Color Accent = new Color(0.72f, 0.30f, 1f);
        private static readonly Color WarnBg = new Color(0.36f, 0.29f, 0f);
        private static readonly Color WarnText = new Color(1f, 0.84f, 0.31f);

        private static readonly ConditionalWeakTable<EditorWindow, Label> Badges =
            new ConditionalWeakTable<EditorWindow, Label>();

        private static bool brokenThisSession;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var windowType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.VRCFProgressWindow"),
                "VF.VRCFProgressWindow"
            );
            var create = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(
                    windowType, "Create",
                    method => method.IsStatic && method.GetParameters().Length == 0
                ),
                "VRCFProgressWindow.Create()"
            );
            var progress = ReflectionUtils.Demand(
                ReflectionUtils.FindMethodWithSignature(
                    windowType, "Progress", typeof(void), typeof(float), typeof(string)
                ),
                "VRCFProgressWindow.Progress(float, string)"
            );

            harmony.Patch(
                create,
                postfix: new HarmonyMethod(typeof(ProgressWindowThemePatch), nameof(CreatePostfix))
            );
            harmony.Patch(
                progress,
                postfix: new HarmonyMethod(typeof(ProgressWindowThemePatch), nameof(ProgressPostfix))
            );
        }

        // object-typed __result: the window type is internal (and this is the shared-patch-safe shape).
        private static void CreatePostfix(object __result) {
            if (brokenThisSession) return;
            var module = ProgressWindowThemeModule.Instance;
            if (module == null || !ModuleRegistry.IsActive(module) || !module.Enabled) {
                // Master switch off is exactly a state worth showing — but with the module
                // itself killed, honor the kill switch and leave the window stock.
                if (Settings.MasterEnabled) return;
            }

            try {
                if (!(__result is EditorWindow window)) return;
                var root = window.rootVisualElement;
                if (root == null) return;

                string text;
                bool warn;
                var compat = Bootstrap.Compat;
                if (!Settings.MasterEnabled) {
                    text = "FuryPlusPlus disabled (master switch).";
                    warn = true;
                } else if (compat == null) {
                    text = "FuryPlusPlus inactive: " + (Bootstrap.DisabledReason ?? "not initialized");
                    warn = true;
                } else if (!compat.IsExactVersion) {
                    text = $"FuryPlusPlus: VRCFury {compat.PackageVersion} detected — tested with " +
                           $"{VrcfuryCompat.PinnedVersion}. Optimizations disabled (profiling only).";
                    warn = true;
                } else {
                    var active = ModuleRegistry.All.Count(m => ModuleRegistry.IsActive(m) && m.Enabled);
                    text = $"FuryPlusPlus — {active} modules active";
                    warn = false;
                }

                var frameColor = warn ? WarnText : Accent;
                root.style.borderTopWidth = 2;
                root.style.borderBottomWidth = 2;
                root.style.borderLeftWidth = 2;
                root.style.borderRightWidth = 2;
                root.style.borderTopColor = frameColor;
                root.style.borderBottomColor = frameColor;
                root.style.borderLeftColor = frameColor;
                root.style.borderRightColor = frameColor;

                // Accent the progress fill regardless of which ProgressBar type VRCFury used.
                var fill = root.Q(className: "unity-progress-bar__progress");
                if (fill != null && !warn) fill.style.backgroundColor = Accent;

                var badge = new Label(text);
                badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                badge.style.fontSize = 10;
                badge.style.paddingTop = 3;
                badge.style.paddingBottom = 3;
                badge.style.paddingLeft = 6;
                badge.style.paddingRight = 6;
                badge.style.whiteSpace = WhiteSpace.Normal;
                if (warn) {
                    badge.style.backgroundColor = WarnBg;
                    badge.style.color = WarnText;
                    badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                } else {
                    badge.style.color = Accent;
                }
                root.Add(badge);

                if (!warn) {
                    Badges.Remove(window);
                    Badges.Add(window, badge);
                }
            } catch (Exception e) {
                brokenThisSession = true;
                Log.Warn("Progress window theme disabled: " + e.Message);
            }
        }

        private static void ProgressPostfix(object __instance) {
            if (brokenThisSession) return;
            try {
                if (!(__instance is EditorWindow window)) return;
                if (!Badges.TryGetValue(window, out var badge)) return;

                var module = ProgressWindowThemeModule.Instance;
                if (module == null || !Settings.IsOptionEnabled(module, ProgressWindowThemeModule.LiveTimings)) {
                    return;
                }
                var current = ProfilePatches.CurrentAction();
                var active = ModuleRegistry.All.Count(m => ModuleRegistry.IsActive(m) && m.Enabled);
                badge.text = current == null
                    ? $"FuryPlusPlus — {active} modules active"
                    : $"FuryPlusPlus — {active} modules active  •  {current.Value.Key} {current.Value.ElapsedMs:F0} ms";
            } catch (Exception e) {
                brokenThisSession = true;
                Log.Warn("Progress window theme disabled: " + e.Message);
            }
        }
    }
}
