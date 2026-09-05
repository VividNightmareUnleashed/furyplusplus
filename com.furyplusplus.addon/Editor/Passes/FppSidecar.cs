using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
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

        internal static string SidecarDirectoryOverride;
        internal static string VrcfuryDesktopDirectoryOverride;

        private static string DirPath => SidecarDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FuryPlusPlus", "SyncData");

        private static string VrcfuryDesktopDirPath => VrcfuryDesktopDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCFury", "DesktopSyncData");

        private static string FileFor(string blueprintId) {
            if (!IsValidBlueprintId(blueprintId)) return null;
            return Path.Combine(DirPath, blueprintId + ".json");
        }

        internal static bool IsValidBlueprintId(string blueprintId) {
            return !string.IsNullOrWhiteSpace(blueprintId)
                   && blueprintId != "."
                   && blueprintId != ".."
                   && !Path.IsPathRooted(blueprintId)
                   && blueprintId.IndexOf('/') < 0
                   && blueprintId.IndexOf('\\') < 0
                   && blueprintId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /**
         * The compressor algorithm inputs that must match between a desktop and a mobile
         * build of the same avatar. The decisions themselves replay through VRCFury's own
         * alignment file; these inputs are the only extra state our deterministic
         * geometry transforms depend on.
         */
        internal static (bool LanePacking, string Sub8List) CurrentCompressorInputs() {
            var lanePacking = ModuleRegistry.IsOn(CompressorLanePackingModule.Instance);
            var sub8List = ModuleRegistry.IsOn(CompressorSub8Module.Instance)
                ? Globs.Normalize(Settings.GetListOption(
                    CompressorSub8Module.Instance, CompressorSub8Module.PrecisionList))
                : "";
            return (lanePacking, sub8List);
        }

        internal static void SaveDesktopDecision(
            string blueprintId,
            IEnumerable<string> strippedParams,
            IEnumerable<string> narrowedParams = null
        ) {
            var file = FileFor(blueprintId);
            if (file == null) {
                if (!string.IsNullOrEmpty(blueprintId)) {
                    Log.Warn("Could not save cross-platform sync data: invalid blueprint ID.");
                }
                return;
            }
            try {
                Directory.CreateDirectory(DirPath);
                var compressor = CurrentCompressorInputs();
                var data = new SavedData {
                    addonVersion = PackageIdentity.Version,
                    algorithmVersion = AlgorithmVersion,
                    strippedParams = strippedParams.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    narrowedParams = (narrowedParams ?? Enumerable.Empty<string>())
                        .OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    compressorLanePacking = compressor.LanePacking,
                    compressorSub8List = compressor.Sub8List,
                    compressorAlgoVersion = CompressorAlgoVersion
                };
                File.WriteAllText(file, JsonUtility.ToJson(data, true));
            } catch (Exception e) {
                Log.Warn("Could not save cross-platform sync data: " + e.Message);
            }
        }

        /**
         * Returns false (with an error message) when an existing desktop record cannot prove
         * that this mobile build derives the same layout — the caller must fail the build.
         */
        internal static bool VerifyMobileDecision(
            string blueprintId,
            IEnumerable<string> strippedParams,
            out string error,
            IEnumerable<string> narrowedParams = null
        ) {
            error = null;
            if (string.IsNullOrEmpty(blueprintId)) return true;
            var file = FileFor(blueprintId);
            if (file == null) {
                error = "FuryPlusPlus cannot verify cross-platform sync data because the avatar " +
                        "has an invalid blueprint ID. Reattach or upload the avatar blueprint first.";
                return false;
            }

            SavedData saved;
            try {
                if (!File.Exists(file)) return true;
                saved = JsonUtility.FromJson<SavedData>(File.ReadAllText(file));
            } catch (Exception e) {
                error = "FuryPlusPlus cross-platform sync data could not be read safely: " + e.Message +
                        ". Re-upload the desktop version before building for mobile.";
                return false;
            }
            if (saved == null) {
                error = "FuryPlusPlus cross-platform sync data is invalid. Re-upload the desktop " +
                        "version before building for mobile.";
                return false;
            }

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
            if (!TryReadVrcfuryDesktopCompression(
                    blueprintId, out var hasVrcfuryData, out var vrcfuryCompresses, out error)) {
                return false;
            }
            if (hasVrcfuryData && vrcfuryCompresses) {
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

        private static bool TryReadVrcfuryDesktopCompression(
            string blueprintId,
            out bool exists,
            out bool compressed,
            out string error
        ) {
            exists = false;
            compressed = false;
            error = null;
            try {
                var path = Path.Combine(VrcfuryDesktopDirPath, blueprintId + ".json");
                if (!File.Exists(path)) return true;
                exists = true;
                if (TryParseVrcfuryCompression(File.ReadAllText(path), out compressed)) return true;
                error = "VRCFury desktop sync data is invalid, so FuryPlusPlus cannot safely " +
                        "verify the compressor layout. Re-upload the desktop version before " +
                        "building for mobile.";
                return false;
            } catch (Exception e) {
                error = "VRCFury desktop sync data could not be read safely: " + e.Message +
                        ". Re-upload the desktop version before building for mobile.";
                return false;
            }
        }

        internal static bool TryParseVrcfuryCompression(string json, out bool compressed) {
            compressed = false;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try {
                var data = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (!(data["parameters"] is JArray parameters)) return false;
                foreach (var parameter in parameters) {
                    if (!(parameter is JObject entry) || entry["compressed"]?.Type != JTokenType.Boolean) return false;
                    compressed |= (bool)entry["compressed"];
                }
                return true;
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
