using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * The dedicated FuryPlusPlus window (Tools/FuryPlusPlus/Settings…). Renders defensively
     * from ModuleRegistry.GetStatus — it can be opened before the delayCall bootstrap ran,
     * or with VRCFury absent.
     *
     * Layout: a stat-card header (avatar analysis + measured last-bake time with a one-shot
     * stock benchmark), a per-category bake breakdown that overlays the stock baseline
     * (orange) under the last bake (purple) with red/green deltas, then a collapsed
     * "Advanced" foldout holding one toolbar tab per module kind with a green per-module
     * "gain chip" on the right of each row. Cards, rows and chips only ever show real
     * numbers — per-avatar projections from Estimators.Analyze or values measured during a
     * bake (ReportStats, BakeHistory) — never invented estimates, so an empty spot means
     * "no data yet".
     */
    internal class SettingsWindow : EditorWindow {
        private const string TabKey = "FuryPlusPlus.SettingsTab";
        private const string BreakdownKey = "FuryPlusPlus.BakeBreakdown";
        private const string AdvancedKey = "FuryPlusPlus.AdvancedOpen";
        private const string TargetKey = "FuryPlusPlus.AnalyzeTarget";

        private struct TabDef {
            internal ModuleKind Kind;
            internal string Title;
            internal string Note;
            internal (string Title, string[] Ids)[] Groups;
        }

        private static readonly TabDef[] Tabs = {
            new TabDef {
                Kind = ModuleKind.Speed, Title = "Build speed",
                Note = "Output-identical bake speedups. The header above shows your last measured bake " +
                       "— use Benchmark for a true before/after; modules that count their own " +
                       "work show it in green.",
                Groups = new[] {
                    ("Armature & links", new[] {
                        "armatureConstraintIndex", "armaturePhysboneIndex", "armatureSkinIndex",
                        "armatureDestroyIndex", "skipArmatureDebugInfo", "fastArmatureMove",
                    }),
                    ("Paths & rewriting", new[] { "orderedPathRewrite", "mergePathCache" }),
                    ("Asset saving", new[] {
                        "saveAssetsDiscovery", "saveAssetsBatching", "consolidatedAssetContainer",
                        "fastControllerAssetGraph",
                    }),
                    ("Controllers & animation", new[] {
                        "controllerParameterIndex", "getLayersMemo", "animatorIteratorMemo",
                        "layerToTreeLayerIndex", "layerToTreeBindingIndex", "trackingBehaviourIndex",
                        "behaviourContainerFilter", "compressorMemo", "blendshapeBindingCache",
                        "blendshapeBakeRewrite",
                    }),
                    ("SPS", new[] { "spsCoveredRenderer", "spsMaterialProbeCache" }),
                    ("Play-mode iteration", new[] { "playModeSkips", "playModeNoDiskSave", "bakeCacheDryRun" }),
                },
            },
            new TabDef {
                Kind = ModuleKind.Quality, Title = "Quality",
                Note = "Changes the bake output: fewer animator layers, fewer sync bits, less VRAM.",
                Groups = new[] {
                    ("Animator layers", new[] {
                        "fullScopeDbt", "offSideElimination", "toggleSeparateLocal", "toggleFadeTrees",
                        "dbtConsolidation",
                    }),
                    ("Animation clips", new[] { "noOpCurveStrip", "clipDedup" }),
                    ("Parameter compressor (sync bits)", new[] {
                        "compressorLanePacking", "compressorSolver", "compressorEligibility", "compressorSub8",
                    }),
                    ("VRAM", new[] { "blendshapeAutoEnable" }),
                },
            },
            new TabDef {
                Kind = ModuleKind.Pass, Title = "Passes",
                Note = "Standalone SDK preprocessor passes that run on the finished avatar — " +
                       "version-pinned like everything else.",
                Groups = new[] {
                    ("Synced parameters", new[] { "stripUnusedParams", "narrowIntParams" }),
                },
            },
            new TabDef {
                Kind = ModuleKind.Cosmetic, Title = "Extras",
                Note = "Editor visuals and diagnostics; never affects the bake output.",
                Groups = new[] {
                    ("Editor visuals", new[] { "progressWindowTheme", "progressPump", "profiling" }),
                },
            },
        };

        private static GUIStyle gainStyle;
        private static GUIStyle miniWrapStyle;
        private static GUIStyle valueStyle;
        private static GUIStyle valueGreenStyle;
        private static GUIStyle deltaStyle;
        private static GUIStyle redStyle;
        private static GUIStyle redRightStyle;
        private static GUIStyle greenRightStyle;
        private static GUIStyle miniRightStyle;
        private static GUIContent infoIcon;
        private static Color gaugeBack;
        private static Color gaugeFill;
        private static Color zebraTint;
        private static Color bandTint;
        /** VRCFury's own orange (VRCFuryHapticSocketGizmo) — marks the stock baseline. */
        private static readonly Color StockOrange = new Color(1f, 0.5f, 0f);

        private Vector2 scroll;
        // Serialized so it survives script compiles; play-mode transitions destroy scene
        // objects, so the GlobalObjectId in SessionState re-resolves it afterwards.
        [SerializeField] private VRC.SDK3.Avatars.Components.VRCAvatarDescriptor estimateTarget;
        private bool autoPickedTarget;
        private Estimators.Result? analysis;

        internal static void Open() {
            var window = GetWindow<SettingsWindow>(utility: false, title: "FuryPlusPlus", focus: true);
            window.minSize = new Vector2(480, 420);
            window.Show();
        }

        private static void EnsureStyles() {
            if (gainStyle != null) return;
            var green = EditorGUIUtility.isProSkin
                ? new Color(0.45f, 0.86f, 0.45f)
                : new Color(0f, 0.45f, 0f);
            gainStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            gainStyle.normal.textColor = green;
            gainStyle.hover.textColor = green;
            miniWrapStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            valueStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            valueGreenStyle = new GUIStyle(valueStyle);
            valueGreenStyle.normal.textColor = green;
            valueGreenStyle.hover.textColor = green;
            deltaStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            deltaStyle.normal.textColor = green;
            deltaStyle.hover.textColor = green;
            var red = EditorGUIUtility.isProSkin
                ? new Color(0.94f, 0.50f, 0.45f)
                : new Color(0.72f, 0.11f, 0.11f);
            redStyle = new GUIStyle(EditorStyles.miniLabel);
            redStyle.normal.textColor = red;
            redStyle.hover.textColor = red;
            // Fixed right-hand columns of the breakdown rows: no wrapping, right-aligned.
            redRightStyle = new GUIStyle(redStyle) { alignment = TextAnchor.MiddleRight };
            greenRightStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            greenRightStyle.normal.textColor = green;
            greenRightStyle.hover.textColor = green;
            miniRightStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            gaugeBack = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.08f)
                : new Color(0f, 0f, 0f, 0.12f);
            zebraTint = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.03f)
                : new Color(0f, 0f, 0f, 0.03f);
            bandTint = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.06f)
                : new Color(0f, 0f, 0f, 0.07f);
            gaugeFill = new Color(0.72f, 0.30f, 1f); // the progress-window accent purple
            infoIcon = new GUIContent(
                EditorGUIUtility.IconContent("console.infoicon.sml").image,
                "Projections compare the un-baked avatar (the baked result differs).\n" +
                "Bake times vary with editor state — compare benchmark and normal bakes " +
                "back-to-back on the same avatar."
            );
        }

        private void OnGUI() {
            EnsureStyles();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space();
            DrawStatusBanner();
            EditorGUILayout.Space();
            DrawHeader();
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
                var advancedOpen = SessionState.GetBool(AdvancedKey, false);
                var newAdvanced = EditorGUILayout.Foldout(
                    advancedOpen,
                    new GUIContent("⚠ Advanced",
                        "Every individual optimization, grouped by kind. The defaults are the " +
                        "recommended setup; you rarely need to touch these."),
                    true);
                if (newAdvanced != advancedOpen) SessionState.SetBool(AdvancedKey, newAdvanced);
                if (newAdvanced) {
                    var tab = Mathf.Clamp(SessionState.GetInt(TabKey, 0), 0, Tabs.Length - 1);
                    var newTab = GUILayout.Toolbar(tab, Tabs.Select(def => def.Title).ToArray());
                    if (newTab != tab) SessionState.SetInt(TabKey, newTab);
                    EditorGUILayout.Space(2);
                    DrawTab(Tabs[newTab]);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawTab(TabDef def) {
            if (!string.IsNullOrEmpty(def.Note)) {
                EditorGUILayout.LabelField(def.Note, miniWrapStyle);
                EditorGUILayout.Space(4);
            }

            // Registry is the source of truth: anything this tab's curated groups don't
            // mention still shows up under "Other" instead of silently disappearing.
            var remaining = ModuleRegistry.ByKind(def.Kind).ToList();
            foreach (var (title, ids) in def.Groups) {
                var modules = ids
                    .Select(ModuleRegistry.Find)
                    .Where(module => module != null && remaining.Remove(module))
                    .ToList();
                if (modules.Count == 0) continue;
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                foreach (var module in modules) DrawModule(module);
                EditorGUILayout.Space(6);
            }
            if (remaining.Count > 0) {
                EditorGUILayout.LabelField("Other", EditorStyles.boldLabel);
                foreach (var module in remaining) DrawModule(module);
                EditorGUILayout.Space(6);
            }

            if (def.Kind == ModuleKind.Cosmetic) DrawDiagnostics();
        }

        // ---- Header: avatar analysis stat cards + measured bake times -----------------

        private void DrawHeader() {
            if (estimateTarget == null && !autoPickedTarget) {
                autoPickedTarget = true;
                estimateTarget = RestoreTarget()
                    ?? FindObjectOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            }
            using (new EditorGUILayout.HorizontalScope()) {
                var picked = (VRC.SDK3.Avatars.Components.VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                    estimateTarget, typeof(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor), true);
                if (picked != estimateTarget) {
                    estimateTarget = picked;
                    SessionState.SetString(TargetKey, picked == null
                        ? ""
                        : GlobalObjectId.GetGlobalObjectIdSlow(picked).ToString());
                }
                using (new EditorGUI.DisabledScope(estimateTarget == null)) {
                    if (GUILayout.Button("Analyze", GUILayout.Width(80))) {
                        analysis = Estimators.Analyze(estimateTarget);
                    }
                }
                GUILayout.Label(infoIcon, GUILayout.Width(20));
            }
            EditorGUILayout.Space(2);

            var cardWidth = Mathf.Max(106f, (position.width - 42f) / 4f);
            using (new EditorGUILayout.HorizontalScope()) {
                DrawSyncCard(cardWidth);
                DrawFxLayersCard(cardWidth);
                DrawVramCard(cardWidth);
                DrawLastBakeCard(cardWidth);
            }

            if (BakeHistory.BenchmarkPending) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.HelpBox(
                        "Benchmark armed — the next bake runs without FuryPlusPlus modules " +
                        "and records the stock VRCFury time as the baseline.",
                        MessageType.Info);
                    if (GUILayout.Button("Cancel", GUILayout.Width(60), GUILayout.Height(38))) {
                        BakeHistory.BenchmarkPending = false;
                    }
                }
            }

            DrawBakeBreakdown();
        }

        /** The avatar picked before the last domain reload / play-mode round trip. */
        private static VRC.SDK3.Avatars.Components.VRCAvatarDescriptor RestoreTarget() {
            var raw = SessionState.GetString(TargetKey, "");
            if (string.IsNullOrEmpty(raw) || !GlobalObjectId.TryParse(raw, out var id)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id)
                as VRC.SDK3.Avatars.Components.VRCAvatarDescriptor;
        }

        private static EditorGUILayout.VerticalScope Card(float width) {
            return new EditorGUILayout.VerticalScope(
                EditorStyles.helpBox, GUILayout.Width(width), GUILayout.MinHeight(80));
        }

        private static void DrawGauge(float fraction) {
            var rect = GUILayoutUtility.GetRect(10f, 6f, GUILayout.ExpandWidth(true));
            rect.height = 4f;
            EditorGUI.DrawRect(rect, gaugeBack);
            if (fraction > 0f) {
                rect.width = Mathf.Max(2f, rect.width * Mathf.Clamp01(fraction));
                EditorGUI.DrawRect(rect, gaugeFill);
            }
        }

        private void DrawSyncCard(float width) {
            using (Card(width)) {
                GUILayout.Label("Sync budget", EditorStyles.miniLabel);
                var a = analysis;
                if (a?.SyncedBits >= 0) {
                    GUILayout.Label($"{a.Value.SyncedBits} / {a.Value.MaxBits} bits", valueStyle);
                    DrawGauge((float)a.Value.SyncedBits / Mathf.Max(1, a.Value.MaxBits));
                    var reclaimable = (a.Value.StrippableBits > 0 ? a.Value.StrippableBits : 0)
                                      + (a.Value.NarrowableInts > 0 ? a.Value.NarrowableInts * 7 : 0);
                    if (reclaimable > 0) {
                        GUILayout.Label(new GUIContent($"-{reclaimable} bits reclaimable",
                            "Strip unused synced parameters + narrow 0/1 Ints (Passes tab)."), deltaStyle);
                    } else {
                        GUILayout.Label("nothing to reclaim", miniWrapStyle);
                    }
                } else {
                    GUILayout.Label("—", valueStyle);
                    GUILayout.Label(a == null ? "press Analyze" : "no expression parameters", miniWrapStyle);
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawFxLayersCard(float width) {
            using (Card(width)) {
                GUILayout.Label("FX layers", EditorStyles.miniLabel);
                var a = analysis;
                GUILayout.Label(a?.FxLayers >= 0 ? a.Value.FxLayers.ToString() : "—", valueStyle);
                var merged = StatOf("dbtConsolidation", @"mergedLayers=(\d+)");
                if (merged > 0) {
                    GUILayout.Label(new GUIContent($"-{merged} merged last bake",
                        "Direct-blendtree layers consolidated into one during the last bake."), deltaStyle);
                } else if (a?.FxLayers > 1) {
                    GUILayout.Label(new GUIContent("→ blendtree at bake",
                        "Eligible toggle layers collapse into a single direct-blendtree layer " +
                        "(Quality tab); the exact merge count is measured per bake."), miniWrapStyle);
                } else if (a == null) {
                    GUILayout.Label("press Analyze", miniWrapStyle);
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawVramCard(float width) {
            using (Card(width)) {
                GUILayout.Label("VRAM", EditorStyles.miniLabel);
                var a = analysis;
                if (a?.BlendshapeVramBytes > 0) {
                    GUILayout.Label(new GUIContent("-" + Vram(a.Value.BlendshapeVramBytes),
                        "Freed by baking non-animated blendshapes (Quality tab). Approximate — assumes " +
                        "Unity's sparse GPU blendshape layout (40 B per affected vertex per frame); " +
                        "shapes the optimizer protects (e.g. MMD) may be kept."), valueGreenStyle);
                    GUILayout.Label(
                        $"{N(a.Value.NonAnimatedBlendshapes)} idle blendshapes · " +
                        $"{a.Value.BlendshapeMeshes} mesh(es)", miniWrapStyle);
                } else if (a?.NonAnimatedBlendshapes > 0) {
                    GUILayout.Label(N(a.Value.NonAnimatedBlendshapes), valueStyle);
                    GUILayout.Label("idle blendshapes bakeable", miniWrapStyle);
                } else {
                    GUILayout.Label("—", valueStyle);
                    GUILayout.Label(a == null ? "press Analyze" : "nothing to bake", miniWrapStyle);
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawLastBakeCard(float width) {
            using (Card(width)) {
                GUILayout.Label("Last bake", EditorStyles.miniLabel);
                var last = BakeHistory.LastBake;
                var stock = BakeHistory.StockBaseline;
                if (last.HasValue) {
                    GUILayout.Label(new GUIContent(FormatMs(last.Value.TotalMs),
                        $"{AvatarLeaf(last.Value.Avatar)} — {last.Value.Date}"), valueStyle);
                    if (stock.HasValue && stock.Value.Avatar == last.Value.Avatar) {
                        var saved = stock.Value.TotalMs - last.Value.TotalMs;
                        if (saved > 0) {
                            GUILayout.Label(new GUIContent($"-{FormatMs(saved)} vs stock",
                                $"{FormatMs(last.Value.TotalMs)} with FuryPlusPlus vs " +
                                $"{FormatMs(stock.Value.TotalMs)} stock — " +
                                $"{stock.Value.TotalMs / last.Value.TotalMs:0.0}× faster. " +
                                $"Baseline recorded {stock.Value.Date}."), deltaStyle);
                        } else {
                            GUILayout.Label(new GUIContent("no gain vs stock",
                                $"Stock baseline {FormatMs(stock.Value.TotalMs)} ({stock.Value.Date}). " +
                                "Bake times vary with editor state; compare back-to-back bakes."),
                                miniWrapStyle);
                        }
                    }
                } else {
                    GUILayout.Label("—", valueStyle);
                    if (stock.HasValue) {
                        GUILayout.Label(new GUIContent("baseline recorded — bake to compare",
                            $"Stock VRCFury: {FormatMs(stock.Value.TotalMs)} on " +
                            $"{AvatarLeaf(stock.Value.Avatar)} ({stock.Value.Date}). The next " +
                            "normal bake becomes the FuryPlusPlus side of the comparison."),
                            miniWrapStyle);
                    } else {
                        GUILayout.Label("run a bake", miniWrapStyle);
                    }
                }
                GUILayout.FlexibleSpace();
                if (!BakeHistory.BenchmarkPending) {
                    var profiling = ProfilingModule.Instance;
                    var canBenchmark = Settings.MasterEnabled && profiling != null
                                       && ModuleRegistry.IsActive(profiling) && profiling.Enabled;
                    using (new EditorGUI.DisabledScope(!canBenchmark)) {
                        if (GUILayout.Button(new GUIContent("Benchmark",
                                canBenchmark
                                    ? "Runs your next bake with every FuryPlusPlus module disabled " +
                                      "(the profiler stays on to measure) and records the stock " +
                                      "VRCFury time as the baseline. One slow bake, once."
                                    : "Needs the bake profiler installed and enabled (Extras tab)."),
                                EditorStyles.miniButton)) {
                            BakeHistory.BenchmarkPending = true;
                        }
                    }
                }
            }
        }

        private struct PhaseRow {
            internal string Name;
            /** -1 when the phase only appeared in the stock benchmark bake. */
            internal double NewMs;
            /** -1 when no comparable baseline exists or the phase is new-only. */
            internal double StockMs;
        }

        /**
         * Maps profiler phase names (VRCFury "Service.Method" action keys) onto the same
         * categories the module tabs use, so savings read per area instead of per internal.
         */
        private static readonly (string Match, string Category)[] CategoryMap = {
            ("ArmatureLink", "Armature & links"),
            ("ObjectMove", "Armature & links"),
            ("FindAnimatedTransforms", "Armature & links"),
            ("Physbone", "Armature & links"),
            ("SaveAssets", "Asset saving"),
            ("AssetDatabase", "Asset saving"),
            ("Haptic", "SPS & haptics"),
            ("Sps", "SPS & haptics"),
            ("Plug", "SPS & haptics"),
            ("Socket", "SPS & haptics"),
            ("Toggle", "Controllers & animation"),
            ("LayerToTree", "Controllers & animation"),
            ("Controller", "Controllers & animation"),
            ("Animator", "Controllers & animation"),
            ("AvatarBindingState", "Controllers & animation"),
            ("FixMasks", "Controllers & animation"),
            ("AllClips", "Controllers & animation"),
            ("Param", "Controllers & animation"),
            ("Menu", "Controllers & animation"),
        };

        private static string CategoryOf(string phase) {
            foreach (var (match, category) in CategoryMap) {
                if (phase.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) return category;
            }
            return "Other passes";
        }

        private void DrawBakeBreakdown() {
            var last = BakeHistory.LastBake;
            var stock = BakeHistory.StockBaseline;
            // The baseline overlays when it is comparable (same avatar as the last normal
            // bake) or when it is the only bake recorded so far (a fresh benchmark).
            var stockUsable = stock.HasValue
                              && (!last.HasValue || stock.Value.Avatar == last.Value.Avatar);
            var newPhases = last.HasValue
                ? BakeHistory.LastPhases()
                : new List<(string Name, double Ms)>();
            var stockPhases = stockUsable
                ? BakeHistory.StockPhases()
                : new List<(string Name, double Ms)>();
            if (newPhases.Count == 0 && stockPhases.Count == 0) return;
            var open = SessionState.GetBool(BreakdownKey, false);
            var newOpen = EditorGUILayout.Foldout(open,
                newPhases.Count > 0 ? "Last bake breakdown" : "Benchmark breakdown", true);
            if (newOpen != open) SessionState.SetBool(BreakdownKey, newOpen);
            if (!newOpen) return;

            // The red→green delta columns only make sense with both sides measured.
            var compare = newPhases.Count > 0 && stockPhases.Count > 0;

            var rows = new List<PhaseRow>();
            var index = new Dictionary<string, int>();
            foreach (var (name, ms) in newPhases) {
                index[name] = rows.Count;
                rows.Add(new PhaseRow { Name = name, NewMs = ms, StockMs = -1 });
            }
            foreach (var (name, ms) in stockPhases) {
                if (index.TryGetValue(name, out var i)) {
                    var row = rows[i];
                    row.StockMs = ms;
                    rows[i] = row;
                } else {
                    rows.Add(new PhaseRow { Name = name, NewMs = -1, StockMs = ms });
                }
            }

            // The recorder keeps phases down to 1 ms so speedups can't fall out of the
            // record; what is big enough to *show* is decided here instead.
            rows.RemoveAll(row => Math.Max(row.NewMs, row.StockMs) < 25.0);
            if (rows.Count == 0) return;

            if (compare) {
                // Only rows that read as a win survive: phases the baseline never measured
                // (its phase floor pruned them) have nothing to compare against, and phases
                // that got slower are work our own modules deliberately moved around (e.g.
                // toggles now build blendtrees inside ToggleBuilder instead of layers for
                // LayerToTree) — out of context they read as regressions. The full truth
                // stays in the header card totals and the console profile report.
                // The 1.005 tolerance keeps near-ties visible as "unchanged" no matter
                // which way the jitter fell, instead of hiding a 403→405 ms row while
                // showing its 403→402 ms twin.
                var wins = rows.Where(row => row.StockMs >= 0
                    && (row.NewMs < 0 || row.NewMs < row.StockMs * 1.005)).ToList();
                if (wins.Count > 0) rows = wins;
                else compare = false;
            }

            var scale = rows.Max(row => Math.Max(row.NewMs, row.StockMs));
            var groups = rows
                .GroupBy(row => CategoryOf(row.Name))
                .OrderByDescending(group => group.Sum(row => Math.Max(row.StockMs, row.NewMs)));

            foreach (var group in groups) {
                // Headers are pure section labels — sums here just echoed their rows'
                // numbers (most categories have one row) and read as extra measurements.
                var headerRect = EditorGUILayout.BeginHorizontal();
                if (Event.current.type == EventType.Repaint) {
                    EditorGUI.DrawRect(headerRect, bandTint);
                }
                GUILayout.Space(2);
                GUILayout.Label(group.Key, EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                var odd = false;
                foreach (var row in group.OrderByDescending(r => Math.Max(r.StockMs, r.NewMs))) {
                    DrawPhaseRow(row, scale, compare, odd);
                    odd = !odd;
                }
                EditorGUILayout.Space(3);
            }

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(4);
                GUIContent orangeLabel;
                if (!stock.HasValue) {
                    orangeLabel = new GUIContent("VRCFury (Benchmark to record)",
                        "Press Benchmark in the header — one bake runs without FuryPlusPlus " +
                        "and its per-phase times overlay here in orange.");
                } else if (!stockUsable) {
                    orangeLabel = new GUIContent(
                        $"VRCFury (baseline: {AvatarLeaf(stock.Value.Avatar)})",
                        "The baseline was benchmarked on a different avatar — press Benchmark " +
                        "to record one for this avatar.");
                } else {
                    orangeLabel = new GUIContent("VRCFury");
                }
                LegendSwatch(StockOrange, orangeLabel);
                GUILayout.Space(12);
                LegendSwatch(gaugeFill, newPhases.Count > 0
                    ? new GUIContent("Fury++")
                    : new GUIContent("Fury++ (bake to record)",
                        "Run a normal bake — its per-phase times overlay in purple with " +
                        "the savings per phase."));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Full report: Extras → Log last profile report.",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawPhaseRow(PhaseRow row, double scale, bool compare, bool odd) {
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (odd && Event.current.type == EventType.Repaint) {
                EditorGUI.DrawRect(rowRect, zebraTint);
            }
            GUILayout.Space(16);
            var rect = GUILayoutUtility.GetRect(64f, 14f, GUILayout.Width(64f));
            rect.y += 5f;
            rect.height = 5f;
            EditorGUI.DrawRect(rect, gaugeBack);
            if (scale > 0) {
                // Square-root scale: linear collapsed anything under ~2% of the longest
                // phase into the 2 px minimum. Sqrt keeps the bars ordered by size while
                // small phases stay visible; the exact numbers live in the columns.
                if (row.StockMs > 0) {
                    var stockRect = rect;
                    stockRect.width = Mathf.Max(2f, rect.width * BarFraction(row.StockMs, scale));
                    EditorGUI.DrawRect(stockRect, StockOrange);
                }
                if (row.NewMs > 0) {
                    var newRect = rect;
                    newRect.width = Mathf.Max(2f, rect.width * BarFraction(row.NewMs, scale));
                    EditorGUI.DrawRect(newRect, gaugeFill);
                }
            }
            GUILayout.Space(6);
            GUILayout.Label(row.Name, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (compare && row.StockMs >= 0 && row.NewMs >= 0) {
                GUILayout.Label(new GUIContent(FormatMs(row.StockMs), "stock VRCFury"),
                    redRightStyle, GUILayout.Width(52f));
                GUILayout.Label("→", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(16f));
                DrawSpeedDelta(row.NewMs, row.StockMs);
            } else if (compare && row.NewMs < 0) {
                // A phase that no longer runs is the biggest win a row can show — style
                // it like one (red stock time → green outcome), not like an error.
                GUILayout.Label(new GUIContent(FormatMs(row.StockMs), "stock VRCFury"),
                    redRightStyle, GUILayout.Width(52f));
                GUILayout.Label("→", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(16f));
                GUILayout.Label(new GUIContent("skipped",
                    "This phase does not run with FuryPlusPlus — a module skips or " +
                    "eliminates it entirely."),
                    greenRightStyle, GUILayout.Width(120f));
            } else if (compare) {
                GUILayout.Label(GUIContent.none, redRightStyle, GUILayout.Width(52f));
                GUILayout.Label(GUIContent.none, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(16f));
                GUILayout.Label(FormatMs(row.NewMs), miniRightStyle, GUILayout.Width(120f));
            } else {
                GUILayout.Label(FormatMs(row.NewMs >= 0 ? row.NewMs : row.StockMs),
                    miniRightStyle, GUILayout.Width(120f));
            }
            EditorGUILayout.EndHorizontal();
        }

        private static float BarFraction(double ms, double scale) {
            return Mathf.Sqrt(Mathf.Clamp01((float)(ms / scale)));
        }

        private static void LegendSwatch(Color color, GUIContent label) {
            var rect = GUILayoutUtility.GetRect(8f, 14f, GUILayout.Width(8f));
            rect.y += 3f;
            rect.height = 8f;
            EditorGUI.DrawRect(rect, color);
            GUILayout.Space(4);
            GUILayout.Label(label, EditorStyles.miniLabel);
        }

        private void DrawSpeedDelta(double newMs, double stockMs) {
            if (stockMs <= 0) {
                GUILayout.Label(FormatMs(newMs), miniRightStyle, GUILayout.Width(120f));
                return;
            }
            var percent = (1 - newMs / stockMs) * 100;
            if (Math.Abs(percent) < 0.5) {
                // Would render as a green "(0% faster)" — an empty claim; say so in grey.
                GUILayout.Label(new GUIContent($"{FormatMs(newMs)} (unchanged)",
                    "Within run-to-run jitter of the stock baseline."),
                    miniRightStyle, GUILayout.Width(120f));
            } else if (newMs < stockMs) {
                GUILayout.Label(new GUIContent($"{FormatMs(newMs)} ({percent:0}% faster)",
                    $"-{FormatMs(stockMs - newMs)} vs stock VRCFury"),
                    greenRightStyle, GUILayout.Width(120f));
            } else {
                GUILayout.Label(new GUIContent($"{FormatMs(newMs)} ({-percent:0}% slower)",
                    "Bake times vary run to run; small phases jitter."),
                    redRightStyle, GUILayout.Width(120f));
            }
        }

        private static string FormatMs(double ms) {
            if (ms >= 60000) {
                var minutes = (int)(ms / 60000);
                return $"{minutes} m {(ms - minutes * 60000) / 1000.0:0} s";
            }
            if (ms >= 1000) return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            return ms.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static string AvatarLeaf(string path) {
            if (string.IsNullOrEmpty(path)) return "unknown avatar";
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        /** Like Stat, but by module id and hardened against a throwing stats formatter. */
        private static int StatOf(string moduleId, string pattern) {
            try {
                var module = ModuleRegistry.Find(moduleId);
                return module == null ? -1 : Stat(module, pattern);
            } catch {
                return -1;
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
                        "UNSUPPORTED VRCFURY VERSION — ALL FEATURES DISABLED.\n" +
                        $"VRCFury {compat.PackageVersion} detected, but this build is validated only " +
                        $"against {VrcfuryCompat.PinnedVersion}. Avatars bake with stock VRCFury; only " +
                        "the bake profiler and editor visuals stay active. Install VRCFury " +
                        $"{VrcfuryCompat.PinnedVersion} or update FuryPlusPlus.",
                        MessageType.Error
                    );
                }
            } else if (Bootstrap.DisabledReason != null) {
                EditorGUILayout.HelpBox("FuryPlusPlus inactive: " + Bootstrap.DisabledReason, MessageType.Warning);
            } else {
                EditorGUILayout.HelpBox("FuryPlusPlus has not initialized yet.", MessageType.None);
            }
        }

        private void DrawModule(Module module) {
            var status = ModuleRegistry.GetStatus(module);
            var installed = status.State == ModuleState.Installed;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!installed)) {
                    var content = new GUIContent(module.DisplayName, module.Description);
                    var width = EditorStyles.label.CalcSize(content).x + 22f;
                    var enabled = EditorPrefs.GetBool(Settings.ModuleKey(module), module.DefaultEnabled);
                    var toggled = EditorGUILayout.ToggleLeft(content, enabled, GUILayout.Width(width));
                    if (toggled != enabled) Settings.SetModuleEnabled(module, toggled);
                }
                GUILayout.FlexibleSpace();
                if (!installed) {
                    var label = status.State == ModuleState.NotInstalled
                        ? "not installed"
                        : status.State == ModuleState.DisabledIncompatible
                            ? "incompatible"
                            : "failed";
                    GUILayout.Label(new GUIContent("— " + label, status.Message ?? ""), EditorStyles.miniLabel);
                } else {
                    var chip = GainChip(module);
                    if (chip.HasValue) {
                        GUILayout.Label(new GUIContent(chip.Value.Text, chip.Value.Tooltip), gainStyle);
                    }
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

        private void DrawDiagnostics() {
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
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

        // ---- Gain chips -------------------------------------------------------------

        private struct Chip {
            internal string Text;
            internal string Tooltip;
        }

        private static Chip? MakeChip(string text, string tooltip) {
            return new Chip { Text = text, Tooltip = tooltip };
        }

        private static string N(int value) {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string Vram(long bytes) {
            return bytes >= 1024 * 1024
                ? (bytes / (1024f * 1024f)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024f).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        /** First capture of `pattern` in the module's last-bake stats, or -1. */
        private static int Stat(Module module, string pattern) {
            var stats = module.ReportStats();
            if (string.IsNullOrEmpty(stats)) return -1;
            var match = Regex.Match(stats, pattern);
            return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : -1;
        }

        private Chip? GainChip(Module module) {
            try {
                return GainChipUnsafe(module);
            } catch {
                return null; // a malformed stats string must never break the window
            }
        }

        private Chip? GainChipUnsafe(Module module) {
            var a = analysis;
            switch (module.Id) {
                case "stripUnusedParams": {
                        if (a?.StrippableBits > 0) {
                            return MakeChip($"-{a.Value.StrippableBits} sync bits",
                                $"{a.Value.StrippableParams} unused synced parameter(s) on the analyzed avatar.");
                        }
                        var bits = Stat(module, @"bits=(\d+)");
                        return bits > 0 ? MakeChip($"-{bits} sync bits last bake", module.ReportStats()) : null;
                    }
                case "narrowIntParams": {
                        if (a?.NarrowableInts > 0) {
                            return MakeChip($"-{a.Value.NarrowableInts * 7} sync bits",
                                $"{a.Value.NarrowableInts} 0/1 Int parameter(s) narrowable to Bool (7 bits each).");
                        }
                        var bits = Stat(module, @"bits=(\d+)");
                        return bits > 0 ? MakeChip($"-{bits} sync bits last bake", module.ReportStats()) : null;
                    }
                case "fullScopeDbt":
                    return a?.FxLayers > 1
                        ? MakeChip($"{a.Value.FxLayers} FX layers → blendtree",
                            "Eligible toggle layers merge into a single direct-blendtree layer; " +
                            "the exact count depends on per-layer eligibility.")
                        : null;
                case "dbtConsolidation": {
                        var merged = Stat(module, @"mergedLayers=(\d+)");
                        return merged > 0 ? MakeChip($"-{merged} layers last bake", module.ReportStats()) : null;
                    }
                case "blendshapeAutoEnable": {
                        if (a?.BlendshapeVramBytes > 0) {
                            return MakeChip($"-{Vram(a.Value.BlendshapeVramBytes)} VRAM",
                                $"{N(a.Value.NonAnimatedBlendshapes)} non-animated blendshapes across " +
                                $"{a.Value.BlendshapeMeshes} mesh(es). Approximate — assumes Unity's sparse GPU " +
                                "blendshape layout (40 B per affected vertex per frame); shapes the optimizer " +
                                "protects (e.g. MMD) may be kept.");
                        }
                        return a?.NonAnimatedBlendshapes > 0
                            ? MakeChip($"{N(a.Value.NonAnimatedBlendshapes)} blendshapes bakeable",
                                $"Non-animated blendshapes across {a.Value.BlendshapeMeshes} mesh(es); " +
                                "baking them frees VRAM (exact amount depends on affected vertices).")
                            : null;
                    }
                case "noOpCurveStrip": {
                        var curves = Stat(module, @"curves=(\d+)");
                        return curves > 0 ? MakeChip($"{N(curves)} curves stripped last bake", module.ReportStats()) : null;
                    }
                case "clipDedup": {
                        var duplicates = Stat(module, @"duplicates=(\d+)");
                        return duplicates > 0
                            ? MakeChip($"{duplicates} duplicate clips removed last bake", module.ReportStats())
                            : null;
                    }
                case "offSideElimination": {
                        var upgraded = Stat(module, @"oneSided=(\d+)");
                        return upgraded > 0 ? MakeChip($"{upgraded} toggles one-sided last bake", module.ReportStats()) : null;
                    }
                case "toggleSeparateLocal": {
                        var converted = Stat(module, @"converted=(\d+)");
                        return converted > 0
                            ? MakeChip($"{converted} toggles → blendtree last bake", module.ReportStats())
                            : null;
                    }
                case "toggleFadeTrees": {
                        var converted = Stat(module, @"converted=(\d+)");
                        return converted > 0
                            ? MakeChip($"{converted} fade toggles converted last bake", module.ReportStats())
                            : null;
                    }
                case "compressorLanePacking": {
                        var stats = module.ReportStats();
                        if (string.IsNullOrEmpty(stats)) return null;
                        var batches = Regex.Match(stats, @"batches=(\d+)->(\d+)");
                        if (!batches.Success) return null;
                        var before = int.Parse(batches.Groups[1].Value);
                        var after = int.Parse(batches.Groups[2].Value);
                        return before > after
                            ? MakeChip($"sync rounds {before} → {after} last bake", stats)
                            : null;
                    }
                case "compressorSolver": {
                        var batches = Stat(module, @"^(\d+) batches per sync");
                        return batches > 0
                            ? MakeChip($"{batches} batches per sync last bake", module.ReportStats())
                            : null;
                    }
                case "compressorEligibility": {
                        var added = Stat(module, @"extraParams=(\d+)");
                        return added > 0
                            ? MakeChip($"+{added} params compressed last bake", module.ReportStats())
                            : null;
                    }
                case "compressorSub8": {
                        var pairs = Stat(module, @"packedPairs=(\d+)");
                        return pairs > 0
                            ? MakeChip($"{pairs} float pairs → 4 bits last bake", module.ReportStats())
                            : null;
                    }
                case "playModeNoDiskSave": {
                        var skipped = Stat(module, @"skippedWrites=(\d+)");
                        return skipped > 0
                            ? MakeChip($"{N(skipped)} disk writes skipped last bake", module.ReportStats())
                            : null;
                    }
                case "bakeCacheDryRun": {
                        var hits = Stat(module, @"wouldHit=(\d+)");
                        var misses = Stat(module, @"wouldMiss=(\d+)");
                        return hits >= 0 && misses >= 0 && hits + misses > 0
                            ? MakeChip($"would hit {hits}/{hits + misses} bakes",
                                "How often an incremental bake cache could have skipped the bake.")
                            : null;
                    }
                case "behaviourContainerFilter": {
                        var stats = module.ReportStats();
                        if (string.IsNullOrEmpty(stats)) return null;
                        var ratio = Regex.Match(stats, @"=(\d+)/(\d+)");
                        return ratio.Success && int.Parse(ratio.Groups[1].Value) > 0
                            ? MakeChip($"{ratio.Groups[1].Value}/{ratio.Groups[2].Value} layer scans skipped last bake", stats)
                            : null;
                    }
                case "spsCoveredRenderer": {
                        var stats = module.ReportStats();
                        if (string.IsNullOrEmpty(stats)) return null;
                        var ratio = Regex.Match(stats, @"^(\d+)/(\d+)$");
                        return ratio.Success && int.Parse(ratio.Groups[1].Value) > 0
                            ? MakeChip($"{ratio.Groups[1].Value}/{ratio.Groups[2].Value} SPS probes skipped last bake", stats)
                            : null;
                    }
                default:
                    return null;
            }
        }
    }
}
