using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class OrderedPathResolverTests {
        [Test]
        public void EmptyRuleSetReturnsInput() {
            var resolver = Resolver();

            Assert.That(resolver.Rewrite("Armature/Hips"), Is.EqualTo("Armature/Hips"));
            Assert.That(resolver.Rewrite(""), Is.EqualTo(""));
        }

        [Test]
        public void RewritesOnlyExactPathsAndSegmentBoundaryDescendants() {
            var resolver = Resolver(("Arm", "Rig"));

            Assert.That(resolver.Rewrite("Arm"), Is.EqualTo("Rig"));
            Assert.That(resolver.Rewrite("Arm/Hand"), Is.EqualTo("Rig/Hand"));
            Assert.That(resolver.Rewrite("Arm/"), Is.EqualTo("Rig/"));
            Assert.That(resolver.Rewrite("Armature"), Is.EqualTo("Armature"));
            Assert.That(resolver.Rewrite("Arm_Ext"), Is.EqualTo("Arm_Ext"));
        }

        [Test]
        public void LaterRulesSeeEarlierRewriteResults() {
            var resolver = Resolver(
                ("Clothes", "Avatar/Hips"),
                ("Avatar/Hips", "Avatar/Rig/Hips"),
                ("Avatar/Rig/Hips/Hand", "Avatar/Hand")
            );

            Assert.That(resolver.Rewrite("Clothes/Hand/Finger"), Is.EqualTo("Avatar/Hand/Finger"));
        }

        [Test]
        public void LaterRuleForOriginalPathDoesNotMatchAfterEarlierMove() {
            var resolver = Resolver(
                ("A", "B"),
                ("A/Child", "Wrong")
            );

            Assert.That(resolver.Rewrite("A/Child"), Is.EqualTo("B/Child"));
        }

        [Test]
        public void EarlierSpecificRuleWinsBeforeLaterAncestorRule() {
            var resolver = Resolver(
                ("A/Child", "Specific"),
                ("A", "Ancestor")
            );

            Assert.That(resolver.Rewrite("A/Child/Leaf"), Is.EqualTo("Specific/Leaf"));
        }

        [Test]
        public void DuplicateSourcesUseChronologicalLowerBound() {
            var resolver = Resolver(
                ("A", "B"),
                ("A", "NeverReached"),
                ("B", "C"),
                ("C", "A"),
                ("A/Leaf", "Done")
            );

            Assert.That(resolver.Rewrite("A/Leaf"), Is.EqualTo("Done"));
        }

        [Test]
        public void EmptySourceMatchesOnlyEmptyPathOrLeadingSlash() {
            var resolver = Resolver(("", "Root"));

            Assert.That(resolver.Rewrite(""), Is.EqualTo("Root"));
            Assert.That(resolver.Rewrite("/Child"), Is.EqualTo("Root/Child"));
            Assert.That(resolver.Rewrite("Child"), Is.EqualTo("Child"));
        }

        [Test]
        public void EmptyDestinationCanFeedALaterEmptySourceRule() {
            var resolver = Resolver(
                ("A", ""),
                ("", "Root")
            );

            Assert.That(resolver.Rewrite("A/Child"), Is.EqualTo("Root/Child"));
        }

        [Test]
        public void TrailingSlashInSourcePreservesLiteralBoundarySemantics() {
            var resolver = Resolver(("A/", "B"));

            Assert.That(resolver.Rewrite("A/"), Is.EqualTo("B"));
            Assert.That(resolver.Rewrite("A//Child"), Is.EqualTo("B/Child"));
            Assert.That(resolver.Rewrite("A/Child"), Is.EqualTo("A/Child"));
        }

        [Test]
        public void CanRevisitSamePathAtHigherRuleIndices() {
            var resolver = Resolver(
                ("A", "B"),
                ("B", "A"),
                ("A/Child", "Done")
            );

            Assert.That(resolver.Rewrite("A/Child"), Is.EqualTo("Done"));
        }

        [Test]
        public void UsesOrdinalCharactersForUnicodePaths() {
            var resolver = Resolver(
                ("骨/é", "Rig"),
                ("Rig", "完成")
            );

            Assert.That(resolver.Rewrite("骨/é/指"), Is.EqualTo("完成/指"));
            Assert.That(resolver.Rewrite("骨/e\u0301/指"), Is.EqualTo("骨/e\u0301/指"));
        }

        [Test]
        public void RejectsNullInputs() {
            Assert.Throws<ArgumentNullException>(() => new OrderedPathResolver(null));
            Assert.Throws<ArgumentException>(() => new OrderedPathResolver(
                new List<(string from, string to)> { (null, "B") }
            ));
            Assert.Throws<ArgumentException>(() => new OrderedPathResolver(
                new List<(string from, string to)> { ("A", null) }
            ));

            Assert.Throws<ArgumentNullException>(() => Resolver().Rewrite(null));
        }

        [Test]
        public void RandomizedResultsMatchNaiveChronologicalImplementation() {
            const int seed = 1597463007;
            var random = new Random(seed);

            for (var scenario = 0; scenario < 250; scenario++) {
                var rules = BuildRandomRules(random, random.Next(0, 50));
                var resolver = new OrderedPathResolver(rules);

                for (var query = 0; query < 80; query++) {
                    var path = BuildRandomQuery(random, rules);
                    var expected = RewriteNaively(path, rules);
                    var actual = resolver.Rewrite(path);

                    if (actual != expected) {
                        Assert.Fail(
                            $"seed={seed}, scenario={scenario}, query={query}, path='{path}', " +
                            $"actual='{actual}', expected='{expected}', rules={FormatRules(rules)}"
                        );
                    }
                }
            }
        }

        private static OrderedPathResolver Resolver(params (string from, string to)[] rules) {
            return new OrderedPathResolver(rules);
        }

        private static string RewriteNaively(
            string path,
            IReadOnlyList<(string from, string to)> rules
        ) {
            for (var i = 0; i < rules.Count; i++) {
                var from = rules[i].from;
                if (path == from || path.StartsWith(from + "/", StringComparison.Ordinal)) {
                    path = rules[i].to + path.Substring(from.Length);
                }
            }

            return path;
        }

        private static List<(string from, string to)> BuildRandomRules(Random random, int count) {
            var rules = new List<(string from, string to)>();
            var pathPool = new List<string> {
                "",
                "A",
                "A/B",
                "Arm",
                "Armature/Hips",
                "/Leading",
                "Trailing/",
                "Double//Slash",
                "骨/手"
            };

            for (var i = 0; i < count; i++) {
                string from;
                if (rules.Count > 0 && random.Next(100) < 20) {
                    // Duplicates exercise terminal-node lower-bound searches.
                    from = rules[random.Next(rules.Count)].from;
                } else if (random.Next(100) < 55) {
                    from = pathPool[random.Next(pathPool.Count)];
                } else {
                    from = RandomPath(random);
                }

                string to;
                if (rules.Count > 0 && random.Next(100) < 35) {
                    // Previous endpoints create a useful number of chronological chains and cycles.
                    var earlier = rules[random.Next(rules.Count)];
                    to = random.Next(2) == 0 ? earlier.from : earlier.to;
                } else if (random.Next(100) < 45) {
                    to = pathPool[random.Next(pathPool.Count)];
                } else {
                    to = RandomPath(random);
                }

                rules.Add((from, to));
                pathPool.Add(from);
                pathPool.Add(to);
                pathPool.Add(from + "/" + RandomSegment(random));
                pathPool.Add(to + "/" + RandomSegment(random));
            }

            return rules;
        }

        private static string BuildRandomQuery(
            Random random,
            IReadOnlyList<(string from, string to)> rules
        ) {
            if (rules.Count == 0 || random.Next(100) < 30) {
                return RandomPath(random);
            }

            var rule = rules[random.Next(rules.Count)];
            if (random.Next(100) < 45) {
                return rule.from;
            }

            return rule.from + "/" + RandomPath(random);
        }

        private static string RandomPath(Random random) {
            var segmentCount = random.Next(0, 5);
            if (segmentCount == 0) return "";

            var output = new StringBuilder();
            if (random.Next(100) < 15) output.Append('/');

            for (var i = 0; i < segmentCount; i++) {
                if (i > 0) output.Append('/');
                output.Append(RandomSegment(random));
            }

            if (random.Next(100) < 15) output.Append('/');
            return output.ToString();
        }

        private static string RandomSegment(Random random) {
            var segments = new[] {
                "",
                "A",
                "B",
                "Arm",
                "Armature",
                "Hips",
                "Hand_L",
                "01",
                "é",
                "e\u0301",
                "骨"
            };
            return segments[random.Next(segments.Length)];
        }

        private static string FormatRules(IEnumerable<(string from, string to)> rules) {
            return "[" + string.Join(", ", rules.Select(rule => $"'{rule.from}'->'{rule.to}'")) + "]";
        }
    }
}
