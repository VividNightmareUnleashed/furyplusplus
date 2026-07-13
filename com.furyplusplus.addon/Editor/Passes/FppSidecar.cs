using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Cross-platform decision record. Desktop uploads persist which parameters FuryPlusPlus
     * changed; mobile builds (where VRCFury replays the desktop parameter layout) recompute
     * and compare — any divergence means the platforms would desync, so the mobile build is
     * hard-failed rather than silently corrupted. Never touches VRCFury's own sync file.
     */
    internal static class FppSidecar {
        [Serializable]
        private class SavedData {
            public string addonVersion;
            public int algorithmVersion;
            public List<string> strippedParams = new List<string>();
        }

        internal const int AlgorithmVersion = 1;

        private static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FuryPlusPlus", "SyncData");

        private static string FileFor(string blueprintId) {
            return Path.Combine(DirPath, blueprintId + ".json");
        }

        internal static void SaveDesktopDecision(string blueprintId, IEnumerable<string> strippedParams) {
            if (string.IsNullOrEmpty(blueprintId)) return;
            try {
                Directory.CreateDirectory(DirPath);
                var data = new SavedData {
                    addonVersion = "0.1.0",
                    algorithmVersion = AlgorithmVersion,
                    strippedParams = strippedParams.OrderBy(name => name, StringComparer.Ordinal).ToList()
                };
                File.WriteAllText(FileFor(blueprintId), JsonUtility.ToJson(data, true));
            } catch (Exception e) {
                Log.Warn("Could not save cross-platform sync data: " + e.Message);
            }
        }

        /**
         * Returns false (with an error message) when a desktop decision exists and differs
         * from this mobile build's decision — the caller must fail the build.
         */
        internal static bool VerifyMobileDecision(
            string blueprintId,
            IEnumerable<string> strippedParams,
            out string error
        ) {
            error = null;
            if (string.IsNullOrEmpty(blueprintId)) return true;

            SavedData saved;
            try {
                var file = FileFor(blueprintId);
                if (!File.Exists(file)) return true;
                saved = JsonUtility.FromJson<SavedData>(File.ReadAllText(file));
            } catch (Exception e) {
                Log.Warn("Could not read cross-platform sync data (skipping verification): " + e.Message);
                return true;
            }
            if (saved == null) return true;

            if (saved.algorithmVersion != AlgorithmVersion) {
                error = $"FuryPlusPlus sync data for this avatar was written by a different " +
                        $"algorithm version ({saved.algorithmVersion} vs {AlgorithmVersion}). " +
                        "Re-upload the desktop version first, then build for mobile.";
                return false;
            }

            var mobile = strippedParams.OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (!mobile.SequenceEqual(saved.strippedParams)) {
                var desktopOnly = saved.strippedParams.Except(mobile).ToList();
                var mobileOnly = mobile.Except(saved.strippedParams).ToList();
                error = "FuryPlusPlus parameter decisions differ between the desktop upload and " +
                        "this mobile build — uploading would desync the two platforms. " +
                        (desktopOnly.Count > 0 ? $"Desktop-only: {string.Join(", ", desktopOnly)}. " : "") +
                        (mobileOnly.Count > 0 ? $"Mobile-only: {string.Join(", ", mobileOnly)}. " : "") +
                        "Re-upload the desktop version first (same FuryPlusPlus settings on both).";
                return false;
            }
            return true;
        }
    }
}
