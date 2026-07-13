using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * What the profiler measured on recent bakes — total time, hottest phases — plus the
     * one-shot "benchmark stock VRCFury" baseline, so the settings window can show real
     * measured speed numbers instead of pointing at the console log.
     *
     * The last normal bake lives in EditorPrefs (disposable — the next bake re-records it).
     * The stock baseline is the expensive record the user paid a full unaccelerated bake
     * for, so it lives in UserSettings/FuryPlusPlusBenchmark.json inside the host project:
     * per-project, survives EditorPrefs loss, and never clobbered by normal bakes.
     * Reads/writes happen at bake boundaries or from the settings window, never in hot
     * patch bodies.
     */
    internal static class BakeHistory {
        private const string Prefix = Settings.KeyPrefix + "bake.";
        private const string BenchmarkKey = Prefix + "benchmarkPending";
        private const string LastKey = Prefix + "last";
        /** Legacy: versions before the baseline file stored the stock record here. */
        private const string StockKey = Prefix + "stock";

        internal struct Record {
            internal double TotalMs;
            /** Avatar scene path captured at bake start; "" when unknown. */
            internal string Avatar;
            /** Local time, "yyyy-MM-dd HH:mm". */
            internal string Date;
        }

        [Serializable]
        private class BaselineFile {
            public double totalMs;
            public string avatar = "";
            public string date = "";
            public string phases = "";
        }

        private static BaselineFile baselineCache;
        private static bool baselineLoaded;
        private static string baselineFileOverride;

        /** Test hook: redirects the baseline file and drops the cache. */
        internal static string BaselineFileOverride {
            get { return baselineFileOverride; }
            set {
                baselineFileOverride = value;
                baselineCache = null;
                baselineLoaded = false;
            }
        }

        /**
         * Arms one benchmark bake: Settings.IsModuleEnabled reports every module except the
         * profiler as disabled until ProfilePatches clears the flag at that bake's end.
         */
        internal static bool BenchmarkPending {
            get { return EditorPrefs.GetBool(BenchmarkKey, false); }
            set { EditorPrefs.SetBool(BenchmarkKey, value); }
        }

        internal static Record? LastBake => ReadPrefs(LastKey);

        internal static Record? StockBaseline {
            get {
                var file = Baseline();
                if (file == null || file.totalMs <= 0) return null;
                return new Record {
                    TotalMs = file.totalMs,
                    Avatar = file.avatar ?? "",
                    Date = file.date ?? "",
                };
            }
        }

        /** Meaningful phases of the last normal bake as (name, ms), hottest first. */
        internal static List<(string Name, double Ms)> LastPhases() {
            return ParsePhases(EditorPrefs.GetString(LastKey + ".phases", ""));
        }

        /** Meaningful phases of the stock-benchmark bake, hottest first. */
        internal static List<(string Name, double Ms)> StockPhases() {
            return ParsePhases(Baseline()?.phases ?? "");
        }

        internal static void RecordBake(
            double totalMs, string avatar, IReadOnlyList<(string Name, double Ms)> phases, bool stock) {
            if (stock) {
                // The baseline must not clobber the last normal bake it gets compared with.
                baselineCache = new BaselineFile {
                    totalMs = totalMs,
                    avatar = avatar ?? "",
                    date = Now(),
                    phases = SerializePhases(phases),
                };
                baselineLoaded = true;
                SaveBaseline(baselineCache);
            } else {
                WritePrefs(LastKey, totalMs, avatar);
                EditorPrefs.SetString(LastKey + ".phases", SerializePhases(phases));
            }
        }

        internal static string SerializePhases(IReadOnlyList<(string Name, double Ms)> phases) {
            var lines = new List<string>();
            foreach (var (name, ms) in phases) {
                lines.Add(ms.ToString("0.###", CultureInfo.InvariantCulture)
                          + "|" + (name ?? "?").Replace('\n', ' '));
            }
            return string.Join("\n", lines);
        }

        internal static List<(string Name, double Ms)> ParsePhases(string raw) {
            var result = new List<(string Name, double Ms)>();
            if (string.IsNullOrEmpty(raw)) return result;
            foreach (var line in raw.Split('\n')) {
                var split = line.IndexOf('|');
                if (split <= 0) continue;
                if (double.TryParse(line.Substring(0, split), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ms)) {
                    result.Add((line.Substring(split + 1), ms));
                }
            }
            return result;
        }

        private static string BaselinePath() {
            if (!string.IsNullOrEmpty(baselineFileOverride)) return baselineFileOverride;
            var project = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(project, "UserSettings", "FuryPlusPlusBenchmark.json");
        }

        private static BaselineFile Baseline() {
            if (baselineLoaded) return baselineCache;
            baselineLoaded = true;
            try {
                var path = BaselinePath();
                if (File.Exists(path)) {
                    var file = new BaselineFile();
                    EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(path), file);
                    if (file.totalMs > 0) baselineCache = file;
                } else {
                    baselineCache = MigrateLegacyPrefs();
                }
            } catch (Exception e) {
                Log.Warn($"Could not read the benchmark baseline file: {e.Message}");
            }
            return baselineCache;
        }

        /** One-time move of a baseline recorded by pre-file versions into the file. */
        private static BaselineFile MigrateLegacyPrefs() {
            var legacy = ReadPrefs(StockKey);
            if (!legacy.HasValue) return null;
            var file = new BaselineFile {
                totalMs = legacy.Value.TotalMs,
                avatar = legacy.Value.Avatar,
                date = legacy.Value.Date,
                phases = EditorPrefs.GetString(StockKey + ".phases", ""),
            };
            if (SaveBaseline(file)) {
                foreach (var suffix in new[] { ".ms", ".avatar", ".date", ".phases" }) {
                    EditorPrefs.DeleteKey(StockKey + suffix);
                }
            }
            return file;
        }

        private static bool SaveBaseline(BaselineFile file) {
            try {
                var path = BaselinePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, EditorJsonUtility.ToJson(file, true));
                return true;
            } catch (Exception e) {
                Log.Warn($"Could not save the benchmark baseline file: {e.Message}");
                return false;
            }
        }

        private static string Now() {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        private static void WritePrefs(string key, double totalMs, string avatar) {
            EditorPrefs.SetString(key + ".ms", totalMs.ToString("R", CultureInfo.InvariantCulture));
            EditorPrefs.SetString(key + ".avatar", avatar ?? "");
            EditorPrefs.SetString(key + ".date", Now());
        }

        private static Record? ReadPrefs(string key) {
            var raw = EditorPrefs.GetString(key + ".ms", "");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                || ms <= 0) {
                return null;
            }
            return new Record {
                TotalMs = ms,
                Avatar = EditorPrefs.GetString(key + ".avatar", ""),
                Date = EditorPrefs.GetString(key + ".date", ""),
            };
        }
    }
}
