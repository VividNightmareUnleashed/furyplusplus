using System;
using System.Collections.Generic;

namespace FuryPlusPlus {
    /// <summary>
    /// Applies ordered path-prefix rewrites with the same chronological semantics as
    /// VRCFury's ObjectMoveService. A later rule sees the result of every earlier rule.
    /// </summary>
    public sealed class OrderedPathResolver {
        private readonly Rule[] rules;
        private readonly TrieNode root = new TrieNode();

        private struct Rule {
            public readonly string from;
            public readonly string to;

            public Rule(string from, string to) {
                this.from = from;
                this.to = to;
            }
        }

        private sealed class TrieNode {
            public Dictionary<char, TrieNode> children;
            public List<int> ruleIndices;

            public TrieNode GetOrAdd(char value) {
                if (children == null) {
                    children = new Dictionary<char, TrieNode>();
                }

                if (!children.TryGetValue(value, out var child)) {
                    child = new TrieNode();
                    children[value] = child;
                }

                return child;
            }
        }

        public OrderedPathResolver(IReadOnlyList<(string from, string to)> rewrites) {
            if (rewrites == null) throw new ArgumentNullException(nameof(rewrites));

            rules = new Rule[rewrites.Count];
            for (var i = 0; i < rewrites.Count; i++) {
                var from = rewrites[i].Item1;
                var to = rewrites[i].Item2;
                if (from == null) {
                    throw new ArgumentException($"Rewrite at index {i} has a null source path", nameof(rewrites));
                }
                if (to == null) {
                    throw new ArgumentException($"Rewrite at index {i} has a null destination path", nameof(rewrites));
                }

                rules[i] = new Rule(from, to);

                var node = root;
                for (var charIndex = 0; charIndex < from.Length; charIndex++) {
                    node = node.GetOrAdd(from[charIndex]);
                }

                if (node.ruleIndices == null) {
                    node.ruleIndices = new List<int>();
                }
                // Rules are inserted chronologically, so each terminal list is already sorted.
                node.ruleIndices.Add(i);
            }
        }

        public string Rewrite(string path) {
            if (path == null) throw new ArgumentNullException(nameof(path));

            var minimumRuleIndex = 0;
            while (minimumRuleIndex < rules.Length &&
                   TryFindNextRule(path, minimumRuleIndex, out var ruleIndex)) {
                var rule = rules[ruleIndex];
                path = rule.to + path.Substring(rule.from.Length);
                minimumRuleIndex = ruleIndex + 1;
            }

            return path;
        }

        private bool TryFindNextRule(string path, int minimumRuleIndex, out int ruleIndex) {
            var bestRuleIndex = int.MaxValue;
            var node = root;

            ConsiderTerminal(node, path, 0, minimumRuleIndex, ref bestRuleIndex);
            if (bestRuleIndex == minimumRuleIndex) {
                ruleIndex = bestRuleIndex;
                return true;
            }

            for (var charIndex = 0; charIndex < path.Length; charIndex++) {
                if (node.children == null || !node.children.TryGetValue(path[charIndex], out node)) {
                    break;
                }

                ConsiderTerminal(node, path, charIndex + 1, minimumRuleIndex, ref bestRuleIndex);
                if (bestRuleIndex == minimumRuleIndex) {
                    break;
                }
            }

            ruleIndex = bestRuleIndex;
            return bestRuleIndex != int.MaxValue;
        }

        private static void ConsiderTerminal(
            TrieNode node,
            string path,
            int prefixLength,
            int minimumRuleIndex,
            ref int bestRuleIndex
        ) {
            if (node.ruleIndices == null) return;

            // This is the allocation-free equivalent of:
            // path == from || path.StartsWith(from + "/", StringComparison.Ordinal)
            if (prefixLength != path.Length && path[prefixLength] != '/') return;

            var candidate = LowerBound(node.ruleIndices, minimumRuleIndex);
            if (candidate >= 0 && candidate < bestRuleIndex) {
                bestRuleIndex = candidate;
            }
        }

        private static int LowerBound(List<int> sortedIndices, int minimumRuleIndex) {
            var low = 0;
            var high = sortedIndices.Count;
            while (low < high) {
                var middle = low + ((high - low) / 2);
                if (sortedIndices[middle] < minimumRuleIndex) {
                    low = middle + 1;
                } else {
                    high = middle;
                }
            }

            return low < sortedIndices.Count ? sortedIndices[low] : -1;
        }
    }
}
