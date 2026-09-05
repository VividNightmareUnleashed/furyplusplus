using System;
using System.Collections.Generic;
using UnityEngine;

namespace FuryPlusPlus {
    /** Exact release pairs published by the FuryPlusPlus maintainers. */
    internal sealed class CompatibilityCatalog {
        internal const int MaxBytes = 256 * 1024;

        [Serializable]
        private sealed class Document {
            public int schemaVersion;
            public int revision;
            public Approval[] approved;
        }

        [Serializable]
        private sealed class Approval {
            public string furyPlusPlus;
            public string[] vrcfury;
        }

        private readonly Dictionary<string, HashSet<string>> approved;
        internal int Revision { get; }
        internal string Json { get; }

        private CompatibilityCatalog(int revision, string json,
            Dictionary<string, HashSet<string>> approved) {
            Revision = revision;
            Json = json;
            this.approved = approved;
        }

        internal bool Approves(string furyPlusPlus, string vrcfury) {
            return furyPlusPlus != null && vrcfury != null
                   && approved.TryGetValue(furyPlusPlus, out var versions)
                   && versions.Contains(vrcfury);
        }

        internal bool CanReplace(CompatibilityCatalog previous) {
            return previous == null || Revision > previous.Revision
                   || (Revision == previous.Revision && Json == previous.Json);
        }

        internal static bool TryParse(string json, out CompatibilityCatalog catalog) {
            catalog = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return false;
            try {
                var document = JsonUtility.FromJson<Document>(json);
                if (document == null || document.schemaVersion != 1 || document.revision < 1
                    || document.approved == null || document.approved.Length > 4096) return false;

                var pairs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var count = 0;
                foreach (var approval in document.approved) {
                    if (approval == null || !ReleaseVersion.IsStable(approval.furyPlusPlus)
                                         || approval.vrcfury == null
                                         || pairs.ContainsKey(approval.furyPlusPlus)) return false;
                    var versions = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var version in approval.vrcfury) {
                        if (++count > 4096 || !ReleaseVersion.IsStable(version) || !versions.Add(version)) return false;
                    }
                    pairs.Add(approval.furyPlusPlus, versions);
                }
                catalog = new CompatibilityCatalog(document.revision, json, pairs);
                return true;
            } catch (Exception) {
                return false;
            }
        }

    }
}
