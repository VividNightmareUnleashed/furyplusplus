using System.Collections.Generic;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class BakeHistoryTests {
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
    }
}
