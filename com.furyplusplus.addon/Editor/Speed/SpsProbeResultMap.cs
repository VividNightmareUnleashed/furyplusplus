using System;
using System.Collections.Generic;
using System.Text;

namespace FuryPlusPlus {
    /// <summary>
    /// Bounded key-to-bool cache with least-recently-used eviction and a compact string
    /// form ("key=1;key=0;", least recent first), so every persisted probe result shares
    /// a single EditorPrefs entry instead of one permanent registry value per signature.
    /// </summary>
    public sealed class SpsProbeResultMap {
        private readonly int capacity;
        private readonly Dictionary<string, bool> resultsByKey =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<string> keysByRecency = new List<string>();

        public SpsProbeResultMap(int capacity) {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public int Count => keysByRecency.Count;

        /// <summary>True while the map differs from the last serialized form.</summary>
        public bool Dirty { get; private set; }

        public bool TryGet(string key, out bool result) {
            if (!resultsByKey.TryGetValue(key, out result)) return false;
            Touch(key);
            return true;
        }

        public void Set(string key, bool result) {
            if (resultsByKey.TryGetValue(key, out var existing)) {
                if (existing != result) {
                    resultsByKey[key] = result;
                    Dirty = true;
                }
                Touch(key);
                return;
            }

            resultsByKey.Add(key, result);
            keysByRecency.Add(key);
            Dirty = true;
            while (keysByRecency.Count > capacity) {
                resultsByKey.Remove(keysByRecency[0]);
                keysByRecency.RemoveAt(0);
            }
        }

        private void Touch(string key) {
            if (keysByRecency[keysByRecency.Count - 1] == key) return;
            keysByRecency.Remove(key);
            keysByRecency.Add(key);
            Dirty = true;
        }

        public string Serialize() {
            var builder = new StringBuilder(keysByRecency.Count * 36);
            foreach (var key in keysByRecency) {
                builder.Append(key).Append('=').Append(resultsByKey[key] ? '1' : '0').Append(';');
            }
            Dirty = false;
            return builder.ToString();
        }

        public static SpsProbeResultMap Deserialize(string stored, int capacity) {
            var map = new SpsProbeResultMap(capacity);
            if (string.IsNullOrEmpty(stored)) return map;
            foreach (var entry in stored.Split(';')) {
                var separator = entry.IndexOf('=');
                if (separator < 1 || separator != entry.Length - 2) continue;
                var value = entry[entry.Length - 1];
                if (value != '0' && value != '1') continue;
                map.Set(entry.Substring(0, separator), value == '1');
            }
            map.Dirty = false;
            return map;
        }
    }
}
