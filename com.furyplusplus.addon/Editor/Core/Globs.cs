using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FuryPlusPlus {
    /**
     * The one semicolon-separated wildcard-list convention shared by every list setting
     * (compressor precision/eligibility lists, pass keep-lists): entries trimmed, empties
     * dropped, '*' and '?' wildcards, whole-name anchored, culture-invariant matching.
     */
    internal static class Globs {
        internal static List<Regex> Parse(string raw) {
            var globs = new List<Regex>();
            if (string.IsNullOrEmpty(raw)) return globs;
            foreach (var entry in raw.Split(';')) {
                var trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;
                var pattern = "^" + Regex.Escape(trimmed).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                globs.Add(new Regex(pattern, RegexOptions.CultureInvariant));
            }
            return globs;
        }

        /** Canonical form for hashing and cross-build comparison; parses identically to raw. */
        internal static string Normalize(string raw) {
            if (string.IsNullOrEmpty(raw)) return "";
            return string.Join(";", raw.Split(';')
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0));
        }
    }
}
