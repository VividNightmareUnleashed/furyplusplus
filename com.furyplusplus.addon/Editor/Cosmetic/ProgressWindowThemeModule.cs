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
    internal sealed class ProgressWindowThemeModule : Module<ProgressWindowThemeModule> {
        internal static readonly ModuleOption LiveTimings = new ModuleOption(
            "liveTimings", "Show live phase timings", true,
            "Appends the currently-running VRCFury phase and its elapsed time to the badge.");

        internal static readonly ModuleOption LiquidBar = new ModuleOption(
            "liquidBar", "Liquid progress fill", true,
            "Animates the progress bar fill as supercharged liquid (waves, twinkling sparkles, " +
            "glowing leading edge). Off = flat accent color.");

        private static readonly ModuleOption[] AllOptions = { LiquidBar, LiveTimings };

        internal override string Id => "progressWindowTheme";
        internal override string DisplayName => "Progress window theme";
        internal override ModuleKind Kind => ModuleKind.Cosmetic;
        internal override CompatTier RequiredTier => CompatTier.Profiling;
        internal override string SettingsGroup => "Editor visuals";
        internal override string Description =>
            "Themes VRCFury's build progress window and surfaces FuryPlusPlus status " +
            "(including a warning when optimizations are disabled by a version mismatch).";

        internal override System.Collections.Generic.IReadOnlyList<ModuleOption> Options => AllOptions;

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

        private static readonly ConditionalWeakTable<EditorWindow, LiquidFillElement> Liquids =
            new ConditionalWeakTable<EditorWindow, LiquidFillElement>();

        private static bool brokenThisSession;

        // Snapshotted at CreatePostfix (once per bake, a phase boundary): ProgressPostfix
        // fires for every VRCFury phase INSIDE the bake being timed, and neither value can
        // change mid-bake — recounting 40+ modules against EditorPrefs there is pure waste.
        private static int activeCountSnapshot;
        private static bool liveTimingsSnapshot;

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
            if (!ModuleRegistry.IsOn(module)) {
                // Master switch off is exactly a state worth showing — but with the module
                // itself killed, honor the kill switch and leave the window stock.
                if (Settings.MasterEnabled) return;
            }
            liveTimingsSnapshot = module != null
                                  && Settings.IsOptionEnabled(module, ProgressWindowThemeModule.LiveTimings);

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
                    activeCountSnapshot = ModuleRegistry.All.Count(ModuleRegistry.IsOn);
                    text = $"FuryPlusPlus — {activeCountSnapshot} modules active";
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
                // Unity itself sizes this element to the progress fraction, so the liquid
                // overlay clips to the current progress for free.
                var fill = root.Q(className: "unity-progress-bar__progress");
                if (fill != null && !warn) {
                    if (module != null && Settings.IsOptionEnabled(module, ProgressWindowThemeModule.LiquidBar)) {
                        fill.style.backgroundColor = LiquidFillElement.BaseColor;
                        fill.style.overflow = Overflow.Hidden;
                        var liquid = new LiquidFillElement();
                        fill.Add(liquid);
                        Liquids.Remove(window);
                        Liquids.Add(window, liquid);
                        // Let the pump repaint this window from inside VRCFury's phases.
                        ProgressPumpPatch.RegisterWindow(window);
                    } else {
                        fill.style.backgroundColor = Accent;
                    }
                }

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
                // The editor loop is blocked during a synchronous bake, so the scheduler
                // never ticks — each Progress() call advances the liquid animation instead.
                if (Liquids.TryGetValue(window, out var liquid)) liquid.MarkDirtyRepaint();
                if (!Badges.TryGetValue(window, out var badge)) return;

                if (!liveTimingsSnapshot) return;
                var current = ProfilePatches.CurrentAction();
                var active = activeCountSnapshot;
                badge.text = current == null
                    ? $"FuryPlusPlus — {active} modules active"
                    : $"FuryPlusPlus — {active} modules active  •  {current.Value.Key} {current.Value.ElapsedMs:F0} ms";
            } catch (Exception e) {
                brokenThisSession = true;
                Log.Warn("Progress window theme disabled: " + e.Message);
            }
        }
    }

    /**
     * The "supercharged liquid" fill: pure UI Toolkit mesh drawing layered inside the
     * stock progress bar's fill element — gradient body, two counter-scrolling surface
     * waves with a bright crest, a drifting specular streak, twinkling overcharged
     * sparkles and a pulsing glow on the leading edge. All motion runs on wall-clock
     * time, independent of progress: repaints are pumped by the editor scheduler while
     * the editor is responsive and by the Progress() postfix during a bake (a blocked
     * main thread physically cannot repaint between those calls).
     * Any draw exception permanently degrades the element to a flat gradient quad.
     */
    internal sealed class LiquidFillElement : VisualElement {
        /** Deep base painted behind the liquid by the fill element itself. */
        internal static readonly Color BaseColor = new Color(0.12f, 0.03f, 0.21f);

        private static readonly Color BodyTop = new Color(0.63f, 0.28f, 0.96f);
        private static readonly Color BodyBottom = new Color(0.33f, 0.08f, 0.58f);
        private static readonly Color WaveBright = new Color(0.84f, 0.47f, 1f, 0.42f);
        private static readonly Color WaveBrightClear = new Color(0.84f, 0.47f, 1f, 0f);
        private static readonly Color WaveWhite = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color WaveWhiteClear = new Color(1f, 1f, 1f, 0f);
        private static readonly Color CrestColor = new Color(1f, 0.93f, 1f, 0.60f);
        private static readonly Color CrestClear = new Color(1f, 0.93f, 1f, 0f);
        private static readonly Color StreakTop = new Color(1f, 1f, 1f, 0.13f);
        private static readonly Color StreakBottom = new Color(1f, 1f, 1f, 0.03f);
        private static readonly Color GlowColor = new Color(0.97f, 0.80f, 1f);

        private bool broken;

        internal LiquidFillElement() {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.top = 0;
            style.right = 0;
            style.bottom = 0;
            style.overflow = Overflow.Hidden;
            generateVisualContent += OnGenerateVisualContent;
            // Keeps the liquid moving while the editor idles (e.g. the upload prompt).
            schedule.Execute(MarkDirtyRepaint).Every(33);
        }

        private void OnGenerateVisualContent(MeshGenerationContext context) {
            var area = contentRect;
            if (area.width < 2f || area.height < 2f) return;
            if (broken) {
                Quad(context, new Rect(0, 0, area.width, area.height),
                    BodyTop, BodyTop, BodyBottom, BodyBottom);
                return;
            }
            try {
                Draw(context, area.width, area.height);
            } catch (Exception) {
                broken = true;
            }
        }

        private static void Draw(MeshGenerationContext context, float width, float height) {
            // Wrapped so precision stays fine over long editor sessions; 3600 is a common
            // multiple of nothing we use, but every animation below is periodic anyway.
            var time = (float)(EditorApplication.timeSinceStartup % 3600.0);

            Quad(context, new Rect(0, 0, width, height), BodyTop, BodyTop, BodyBottom, BodyBottom);

            // Two counter-scrolling surface waves give the liquid its depth...
            Wave(context, width, height, time,
                height * 0.30f, height * 0.13f, 46f, 2.6f, 0f, WaveBright, WaveBrightClear);
            Wave(context, width, height, time,
                height * 0.20f, height * 0.10f, 73f, -1.9f, 2.1f, WaveWhite, WaveWhiteClear);
            // ...and the crest rides the first one.
            Crest(context, width, height, time,
                height * 0.30f, height * 0.13f, 46f, 2.6f, 0f, 2.4f);

            Streak(context, width, height, time);
            Sparkles(context, width, height, time);
            EdgeGlow(context, width, height, time);
        }

        private static float SurfaceY(float x, float time, float surfaceY, float amplitude,
            float wavelength, float speed, float phase, float height) {
            var y = surfaceY + amplitude * Mathf.Sin(x / wavelength * (2f * Mathf.PI) - time * speed + phase);
            return Mathf.Clamp(y, 0.5f, height);
        }

        /** Fills from a scrolling sine surface down to the bottom edge. */
        private static void Wave(MeshGenerationContext context, float width, float height, float time,
            float surfaceY, float amplitude, float wavelength, float speed, float phase,
            Color top, Color bottom) {
            var segments = Mathf.Clamp((int)(width / 7f), 8, 120);
            var mesh = context.Allocate((segments + 1) * 2, segments * 6);
            for (var i = 0; i <= segments; i++) {
                var x = width * i / segments;
                var y = SurfaceY(x, time, surfaceY, amplitude, wavelength, speed, phase, height);
                mesh.SetNextVertex(new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = top });
                mesh.SetNextVertex(new Vertex { position = new Vector3(x, height, Vertex.nearZ), tint = bottom });
            }
            for (var i = 0; i < segments; i++) {
                var a = (ushort)(i * 2);
                mesh.SetNextIndex(a);
                mesh.SetNextIndex((ushort)(a + 2));
                mesh.SetNextIndex((ushort)(a + 1));
                mesh.SetNextIndex((ushort)(a + 2));
                mesh.SetNextIndex((ushort)(a + 3));
                mesh.SetNextIndex((ushort)(a + 1));
            }
        }

        /** Thin bright ribbon along a wave surface, fading downward. */
        private static void Crest(MeshGenerationContext context, float width, float height, float time,
            float surfaceY, float amplitude, float wavelength, float speed, float phase, float thickness) {
            var segments = Mathf.Clamp((int)(width / 7f), 8, 120);
            var mesh = context.Allocate((segments + 1) * 2, segments * 6);
            for (var i = 0; i <= segments; i++) {
                var x = width * i / segments;
                var y = SurfaceY(x, time, surfaceY, amplitude, wavelength, speed, phase, height);
                mesh.SetNextVertex(new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = CrestColor });
                mesh.SetNextVertex(new Vertex {
                    position = new Vector3(x, Mathf.Min(y + thickness, height), Vertex.nearZ), tint = CrestClear,
                });
            }
            for (var i = 0; i < segments; i++) {
                var a = (ushort)(i * 2);
                mesh.SetNextIndex(a);
                mesh.SetNextIndex((ushort)(a + 2));
                mesh.SetNextIndex((ushort)(a + 1));
                mesh.SetNextIndex((ushort)(a + 2));
                mesh.SetNextIndex((ushort)(a + 3));
                mesh.SetNextIndex((ushort)(a + 1));
            }
        }

        /** Slanted specular band drifting left to right. */
        private static void Streak(MeshGenerationContext context, float width, float height, float time) {
            var cycle = width + 90f;
            var x = time * 55f % cycle - 45f;
            Poly4(context,
                new Vector2(x, 0), new Vector2(x + 18f, 0),
                new Vector2(x + 10f, height), new Vector2(x - 8f, height),
                StreakTop, StreakTop, StreakBottom, StreakBottom);
        }

        /**
         * Overcharged energy glints drifting with the liquid flow. Each sparkle twinkles
         * on its own wall-clock rhythm (a spiky pow-sine so it stays faint most of the
         * time and flares hard), completely independent of the progress value.
         */
        private static void Sparkles(MeshGenerationContext context, float width, float height, float time) {
            if (width < 20f) return;
            for (var i = 0; i < 9; i++) {
                var seed = Frac(i * 0.618034f + 0.271f);
                var seedB = Frac(i * 0.754877f + 0.113f);
                // Drift with the liquid; wrap around the fill horizontally.
                var x = (seed * width + time * (6f + 10f * seedB)) % width;
                var y = height * (0.30f + 0.60f * seedB)
                        + Mathf.Sin(time * 0.9f + i * 1.7f) * height * 0.06f;
                var twinkle = Mathf.Pow(
                    0.5f + 0.5f * Mathf.Sin(time * (2.1f + 2.9f * seed) + i * 2.399f), 6f);
                var alpha = 0.05f + 0.85f * twinkle;
                var size = (1.4f + 2.6f * twinkle) * (0.7f + 0.6f * seedB);
                Star(context, new Vector2(x, y), size, alpha);
            }
        }

        /** Four-point star glint: bright core fading to transparent tips. */
        private static void Star(MeshGenerationContext context, Vector2 center, float size, float alpha) {
            var core = new Color(1f, 0.96f, 1f, alpha);
            var waistColor = new Color(0.95f, 0.75f, 1f, alpha * 0.5f);
            var tipColor = new Color(0.95f, 0.75f, 1f, 0f);
            var waist = size * 0.22f;
            var mesh = context.Allocate(9, 24);
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x, center.y, Vertex.nearZ), tint = core });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x, center.y - size, Vertex.nearZ), tint = tipColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x + waist, center.y - waist, Vertex.nearZ), tint = waistColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x + size, center.y, Vertex.nearZ), tint = tipColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x + waist, center.y + waist, Vertex.nearZ), tint = waistColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x, center.y + size, Vertex.nearZ), tint = tipColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x - waist, center.y + waist, Vertex.nearZ), tint = waistColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x - size, center.y, Vertex.nearZ), tint = tipColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x - waist, center.y - waist, Vertex.nearZ), tint = waistColor });
            for (var k = 1; k <= 8; k++) {
                mesh.SetNextIndex(0);
                mesh.SetNextIndex((ushort)k);
                mesh.SetNextIndex((ushort)(k == 8 ? 1 : k + 1));
            }
        }

        /** Pulsing bright "pour front" on the leading (right) edge. */
        private static void EdgeGlow(MeshGenerationContext context, float width, float height, float time) {
            var pulse = 0.75f + 0.25f * Mathf.Sin(time * 5.2f);
            var glowWidth = Mathf.Min(9f, width);
            var inner = new Color(GlowColor.r, GlowColor.g, GlowColor.b, 0f);
            var outer = new Color(GlowColor.r, GlowColor.g, GlowColor.b, 0.55f * pulse);
            Quad(context, new Rect(width - glowWidth, 0, glowWidth, height), inner, outer, outer, inner);
            var line = new Color(1f, 0.95f, 1f, 0.85f * pulse);
            Quad(context, new Rect(width - 1.5f, 0, 1.5f, height), line, line, line, line);
        }

        private static void Quad(MeshGenerationContext context, Rect rect,
            Color topLeft, Color topRight, Color bottomRight, Color bottomLeft) {
            Poly4(context,
                new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax),
                topLeft, topRight, bottomRight, bottomLeft);
        }

        private static void Poly4(MeshGenerationContext context,
            Vector2 a, Vector2 b, Vector2 c, Vector2 d,
            Color colorA, Color colorB, Color colorC, Color colorD) {
            var mesh = context.Allocate(4, 6);
            mesh.SetNextVertex(new Vertex { position = new Vector3(a.x, a.y, Vertex.nearZ), tint = colorA });
            mesh.SetNextVertex(new Vertex { position = new Vector3(b.x, b.y, Vertex.nearZ), tint = colorB });
            mesh.SetNextVertex(new Vertex { position = new Vector3(c.x, c.y, Vertex.nearZ), tint = colorC });
            mesh.SetNextVertex(new Vertex { position = new Vector3(d.x, d.y, Vertex.nearZ), tint = colorD });
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
        }

        private static float Frac(float value) {
            return value - Mathf.Floor(value);
        }
    }
}
