using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace FuryPlusPlus.Tests.Editor {
    public class BakeHistoryTests {
        private static readonly string[] RecordSuffixes = { ".ms", ".avatar", ".date", ".phases" };

        [Test]
        public void PhasesRoundTrip() {
            var phases = new List<(string Name, double Ms)> {
                ("SaveAssetsService.Run", 5123.456),
                ("LayerToTreeService.Apply", 812.5),
                ("Weird=Name|WithPipe", 3.25),
            };
            var parsed = BakeHistory.ParsePhases(BakeHistory.SerializePhases(phases));
            Assert.That(parsed.Count, Is.EqualTo(3));
            Assert.That(parsed[0].Name, Is.EqualTo("SaveAssetsService.Run"));
            Assert.That(parsed[0].Ms, Is.EqualTo(5123.456).Within(0.001));
            Assert.That(parsed[1].Ms, Is.EqualTo(812.5).Within(0.001));
            // The ms field comes first, so pipes inside phase names survive the round trip.
            Assert.That(parsed[2].Name, Is.EqualTo("Weird=Name|WithPipe"));
        }

        [Test]
        public void SerializeFlattensNewlinesInNames() {
            var phases = new List<(string Name, double Ms)> { ("multi\nline", 1.0) };
            var parsed = BakeHistory.ParsePhases(BakeHistory.SerializePhases(phases));
            Assert.That(parsed.Count, Is.EqualTo(1));
            Assert.That(parsed[0].Name, Is.EqualTo("multi line"));
        }

        [Test]
        public void ParseIgnoresGarbage() {
            Assert.That(BakeHistory.ParsePhases(""), Is.Empty);
            Assert.That(BakeHistory.ParsePhases(null), Is.Empty);
            Assert.That(BakeHistory.ParsePhases("nonsense\n|leadingPipe\nxx|notANumber"), Is.Empty);
        }

        [Test]
        public void BenchmarkForcesModulesOffButKeepsProfiler() {
            var profiling = ProfilingModule.Instance;
            if (profiling == null || !ModuleRegistry.IsActive(profiling)
                || !Settings.IsModuleEnabled(profiling)) {
                Assert.Ignore("Profiling module not installed/enabled in this editor session.");
            }
            var hadFlag = BakeHistory.BenchmarkPending;
            try {
                BakeHistory.BenchmarkPending = true;
                Assert.That(Settings.IsModuleEnabled(profiling), Is.True,
                    "the profiler must keep measuring during a stock benchmark");
                foreach (var module in ModuleRegistry.All) {
                    if (module is ProfilingModule) continue;
                    Assert.That(Settings.IsModuleEnabled(module), Is.False,
                        $"{module.Id} must read as disabled while the stock benchmark is armed");
                }
            } finally {
                BakeHistory.BenchmarkPending = hadFlag;
            }
        }

        [Test]
        public void StockBakeGoesToFileAndDoesNotOverwriteLastBake() {
            var file = TempBaselinePath();
            var savedLast = Snapshot(Settings.KeyPrefix + "bake.last");
            try {
                BakeHistory.BaselineFileOverride = file;
                BakeHistory.RecordBake(1000, "Av",
                    new List<(string, double)> { ("PhaseA", 800) }, stock: false);
                BakeHistory.RecordBake(5000, "Av",
                    new List<(string, double)> { ("PhaseA", 4000) }, stock: true);

                Assert.That(BakeHistory.LastBake.Value.TotalMs, Is.EqualTo(1000).Within(0.01),
                    "a stock benchmark must not clobber the last normal bake");
                Assert.That(BakeHistory.StockBaseline.Value.TotalMs, Is.EqualTo(5000).Within(0.01));
                Assert.That(File.Exists(file), "the baseline must be persisted to disk");

                // Setting the override again drops the cache: proves a fresh read from disk.
                BakeHistory.BaselineFileOverride = file;
                Assert.That(BakeHistory.StockBaseline.Value.TotalMs, Is.EqualTo(5000).Within(0.01));
                Assert.That(BakeHistory.StockPhases().Single().Name, Is.EqualTo("PhaseA"));
                Assert.That(BakeHistory.StockPhases().Single().Ms, Is.EqualTo(4000).Within(0.01));
            } finally {
                Restore(savedLast);
                BakeHistory.BaselineFileOverride = null;
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [Test]
        public void LegacyPrefsBaselineMigratesToFile() {
            var file = TempBaselinePath();
            var stockKey = Settings.KeyPrefix + "bake.stock";
            var savedStock = Snapshot(stockKey);
            try {
                EditorPrefs.SetString(stockKey + ".ms", "1234.5");
                EditorPrefs.SetString(stockKey + ".avatar", "LegacyAv");
                EditorPrefs.SetString(stockKey + ".date", "2026-01-01 00:00");
                EditorPrefs.SetString(stockKey + ".phases", "1000|PhaseZ");
                BakeHistory.BaselineFileOverride = file; // cache dropped; file absent

                var baseline = BakeHistory.StockBaseline;
                Assert.That(baseline.HasValue, "a legacy prefs baseline must still be readable");
                Assert.That(baseline.Value.TotalMs, Is.EqualTo(1234.5).Within(0.01));
                Assert.That(baseline.Value.Avatar, Is.EqualTo("LegacyAv"));
                Assert.That(BakeHistory.StockPhases().Single().Name, Is.EqualTo("PhaseZ"));
                Assert.That(File.Exists(file), "migration must write the baseline file");
                Assert.That(EditorPrefs.HasKey(stockKey + ".ms"), Is.False,
                    "legacy keys are deleted after a successful migration");
            } finally {
                Restore(savedStock);
                BakeHistory.BaselineFileOverride = null;
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private static string TempBaselinePath() {
            return Path.Combine(Path.GetTempPath(),
                "fpp-baseline-test-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static Dictionary<string, string> Snapshot(string baseKey) {
            var result = new Dictionary<string, string>();
            foreach (var suffix in RecordSuffixes) {
                var key = baseKey + suffix;
                result[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key) : null;
            }
            return result;
        }

        private static void Restore(Dictionary<string, string> snapshot) {
            foreach (var pair in snapshot) {
                if (pair.Value == null) EditorPrefs.DeleteKey(pair.Key);
                else EditorPrefs.SetString(pair.Key, pair.Value);
            }
        }
    }
}
