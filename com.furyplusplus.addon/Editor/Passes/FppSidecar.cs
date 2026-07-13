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
            public List<string> narrowedParams = new List<string>();
            // Compressor algorithm inputs (absent in v1 files ⇒ defaults ⇒ "features off",
            // which is correct: those uploads predate the compressor modules).
            public bool compressorLanePacking;
            public string compressorSub8List = "";
            public int compressorAlgoVersion;
        }

        internal const int AlgorithmVersion = 1;

        /** Bump when the lane-packing/sub-8 batch geometry algorithm changes shape. */
        internal const int CompressorAlgoVersion = 1;

        private static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FuryPlusPlus", "SyncData");

        private static string FileFor(string blueprintId) {
            return Path.Combine(DirPath, blueprintId + ".json");
        }

        /**
         * The compressor algorithm inputs that must match between a desktop and a mobile
         * build of the same avatar. The decisions themselves replay through VRCFury's own
         * alignment file; these inputs are the only extra state our deterministic
         * geometry transforms depend on.
         */
        internal static (bool LanePacking, string Sub8List) CurrentCompressorInputs() {
            var lanePacking = CompressorLanePackingModule.Instance != null
                              && ModuleRegistry.IsActive(CompressorLanePackingModule.Instance)
                              && CompressorLanePackingModule.Instance.Enabled;
            var sub8On = CompressorSub8Module.Instance != null
                         && ModuleRegistry.IsActive(CompressorSub8Module.Instance)
                         && CompressorSub8Module.Instance.Enabled;
            var sub8List = sub8On
                ? NormalizeList(UnityEditor.EditorPrefs.GetString(CompressorScope.Sub8ListKey, ""))
                : "";
            return (lanePacking, sub8List);
        }

        private static string NormalizeList(string raw) {
            return string.Join(";", raw.Split(';')
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0));
        }

        internal static void SaveDesktopDecision(
            string blueprintId,
            IEnumerable<string> strippedParams,
            IEnumerable<string> narrowedParams = null
        ) {
            if (string.IsNullOrEmpty(blueprintId)) return;
            try {
                Directory.CreateDirectory(DirPath);
                var compressor = CurrentCompressorInputs();
                var data = new SavedData {
                    addonVersion = "0.1.0",
                    algorithmVersion = AlgorithmVersion,
                    strippedParams = strippedParams.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    narrowedParams = (narrowedParams ?? Enumerable.Empty<string>())
                        .OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    compressorLanePacking = compressor.LanePacking,
                    compressorSub8List = compressor.Sub8List,
                    compressorAlgoVersion = CompressorAlgoVersion
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
            out string error,
            IEnumerable<string> narrowedParams = null
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

            if (!SetsMatch(saved.strippedParams, strippedParams, "un-synced", out error)) return false;
            if (!SetsMatch(saved.narrowedParams, narrowedParams ?? Enumerable.Empty<string>(),
                    "narrowed", out error)) {
                return false;
            }

            // Compressor inputs only matter when the compressor actually engages, which
            // (on mobile) means VRCFury's own desktop sync file marked params compressed.
            if (VrcfuryDesktopDataCompresses(blueprintId)) {
                var current = CurrentCompressorInputs();
                if (saved.compressorLanePacking != current.LanePacking
                    || (saved.compressorSub8List ?? "") != current.Sub8List
                    || (saved.compressorLanePacking || (saved.compressorSub8List ?? "") != "")
                       && saved.compressorAlgoVersion != CompressorAlgoVersion) {
                    error = "FuryPlusPlus compressor settings differ between the desktop upload and " +
                            "this mobile build — the two platforms would derive different sync " +
                            "layouts and desync. Desktop: lanePacking=" + saved.compressorLanePacking +
                            $", sub8List='{saved.compressorSub8List}' (algo v{saved.compressorAlgoVersion}). " +
                            $"This build: lanePacking={current.LanePacking}, sub8List='{current.Sub8List}' " +
                            $"(algo v{CompressorAlgoVersion}). Match the settings, re-upload desktop " +
                            "first, then build for mobile.";
                    return false;
                }
            }
            return true;
        }

        /** True when VRCFury's own desktop sync file for this avatar compressed any param. */
        private static bool VrcfuryDesktopDataCompresses(string blueprintId) {
            try {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VRCFury", "DesktopSyncData", blueprintId + ".json");
                if (!File.Exists(path)) return false;
                // Read-only peek at VRCFury's file (never written by FuryPlusPlus).
                return File.ReadAllText(path).Contains("\"compressed\": true");
            } catch {
                return false;
            }
        }

        private static bool SetsMatch(
            List<string> desktop,
            IEnumerable<string> mobileSource,
            string what,
            out string error
        ) {
            error = null;
            var mobile = mobileSource.OrderBy(name => name, StringComparer.Ordinal).ToList();
            desktop = desktop ?? new List<string>();
            if (mobile.SequenceEqual(desktop)) return true;
            var desktopOnly = desktop.Except(mobile).ToList();
            var mobileOnly = mobile.Except(desktop).ToList();
            error = $"FuryPlusPlus {what}-parameter decisions differ between the desktop upload " +
                    "and this mobile build — uploading would desync the two platforms. " +
                    (desktopOnly.Count > 0 ? $"Desktop-only: {string.Join(", ", desktopOnly)}. " : "") +
                    (mobileOnly.Count > 0 ? $"Mobile-only: {string.Join(", ", mobileOnly)}. " : "") +
                    "Re-upload the desktop version first (same FuryPlusPlus settings on both).";
            return false;
        }
    }
}
