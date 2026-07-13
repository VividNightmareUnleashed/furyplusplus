using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * What the profiler measured on recent bakes — total time, hottest phases — plus the
     * one-shot "benchmark stock VRCFury" baseline, persisted in EditorPrefs so the settings
     * window can show real measured speed numbers instead of pointing at the console log.
     * Reads/writes happen at bake boundaries or from the settings window, never in hot
     * patch bodies.
     */
    internal static class BakeHistory {
        private const string Prefix = Settings.KeyPrefix + "bake.";
        private const string BenchmarkKey = Prefix + "benchmarkPending";
        private const string LastKey = Prefix + "last";
        private const string StockKey = Prefix + "stock";

        internal struct Record {
            internal double TotalMs;
            /** Avatar scene path captured at bake start; "" when unknown. */
            internal string Avatar;
            /** Local time, "yyyy-MM-dd HH:mm". */
            internal string Date;
        }

        /**
         * Arms one benchmark bake: Settings.IsModuleEnabled reports every module except the
         * profiler as disabled until ProfilePatches clears the flag at that bake's end.
         */
        internal static bool BenchmarkPending {
            get { return EditorPrefs.GetBool(BenchmarkKey, false); }
            set { EditorPrefs.SetBool(BenchmarkKey, value); }
        }

        internal static Record? LastBake => Read(LastKey);
        internal static Record? StockBaseline => Read(StockKey);
        internal static bool LastBakeWasStock => EditorPrefs.GetBool(LastKey + ".stock", false);

        /** Meaningful phases of the last bake as (name, ms), hottest first. */
        internal static List<(string Name, double Ms)> LastPhases() {
            return ParsePhases(EditorPrefs.GetString(LastKey + ".phases", ""));
        }

        /** Meaningful phases of the stock-benchmark bake, hottest first. */
        internal static List<(string Name, double Ms)> StockPhases() {
            return ParsePhases(EditorPrefs.GetString(StockKey + ".phases", ""));
        }

        internal static void RecordBake(
            double totalMs, string avatar, IReadOnlyList<(string Name, double Ms)> phases, bool stock) {
            Write(LastKey, totalMs, avatar);
            EditorPrefs.SetBool(LastKey + ".stock", stock);
            var serialized = SerializePhases(phases);
            EditorPrefs.SetString(LastKey + ".phases", serialized);
            if (stock) {
                Write(StockKey, totalMs, avatar);
                EditorPrefs.SetString(StockKey + ".phases", serialized);
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

        private static void Write(string key, double totalMs, string avatar) {
            EditorPrefs.SetString(key + ".ms", totalMs.ToString("R", CultureInfo.InvariantCulture));
            EditorPrefs.SetString(key + ".avatar", avatar ?? "");
            EditorPrefs.SetString(key + ".date", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        }

        private static Record? Read(string key) {
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
