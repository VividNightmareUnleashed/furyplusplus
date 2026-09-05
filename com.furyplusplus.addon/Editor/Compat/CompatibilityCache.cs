using System;
using System.IO;
using UnityEngine;

namespace FuryPlusPlus {
    internal static class CompatibilityCache {
        internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        [Serializable]
        private sealed class Record {
            public long fetchedUtcTicks;
            public string catalog;
        }

        internal static string Serialize(CompatibilityCatalog catalog, DateTime nowUtc) {
            return JsonUtility.ToJson(new Record { fetchedUtcTicks = nowUtc.Ticks, catalog = catalog.Json });
        }

        /** Expired catalogs are retained only to reject older revisions, never to approve a pair. */
        internal static bool TryRead(string json, DateTime nowUtc,
            out CompatibilityCatalog catalog, out bool fresh) {
            catalog = null;
            fresh = false;
            if (string.IsNullOrWhiteSpace(json) || json.Length > CompatibilityCatalog.MaxBytes * 2) return false;
            try {
                var record = JsonUtility.FromJson<Record>(json);
                if (record == null || record.fetchedUtcTicks <= 0
                    || record.fetchedUtcTicks > nowUtc.Ticks
                    || !CompatibilityCatalog.TryParse(record.catalog, out catalog)) return false;
                fresh = nowUtc.Ticks - record.fetchedUtcTicks <= MaxAge.Ticks;
                return true;
            } catch (Exception) {
                catalog = null;
                return false;
            }
        }

        internal static void Write(string path, CompatibilityCatalog catalog, DateTime nowUtc) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try {
                File.WriteAllText(temporary, Serialize(catalog, nowUtc));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            } finally {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
