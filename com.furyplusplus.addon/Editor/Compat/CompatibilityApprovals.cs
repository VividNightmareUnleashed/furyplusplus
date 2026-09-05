using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace FuryPlusPlus {
    /** Downloads data only. The active catalog is immutable for the lifetime of the script domain. */
    internal static class CompatibilityApprovals {
        internal const string CatalogUrl =
            "https://raw.githubusercontent.com/VividNightmareUnleashed/furyplusplus/master/compatibility.json";

        private static bool initialized;
        private static CompatibilityCatalog active;
        private static CompatibilityCatalog latest;
        private static UnityWebRequest request;
        private static BoundedDownload download;
        private static string cachePath;

        internal static bool IsChecking => request != null;
        internal static string Status { get; private set; } = "Approval list has not been checked.";

        internal static void Initialize() {
            if (initialized) return;
            initialized = true;
            cachePath = Path.Combine(Path.GetDirectoryName(Application.dataPath),
                "Library", "FuryPlusPlus", "compatibility-cache.json");
            try {
                if (File.Exists(cachePath)
                    && new FileInfo(cachePath).Length <= CompatibilityCatalog.MaxBytes * 2
                    && CompatibilityCache.TryRead(File.ReadAllText(cachePath), DateTime.UtcNow,
                        out var cached, out var fresh)) {
                    latest = cached;
                    if (fresh) active = cached;
                    Status = fresh ? "Using cached approvals."
                        : "Cached approvals are older than 30 days; a refresh is required.";
                }
            } catch (Exception e) {
                Status = "Could not read cached approvals: " + e.Message;
            }
            AssemblyReloadEvents.beforeAssemblyReload += CancelRequest;
            EditorApplication.quitting += CancelRequest;
            // Batch imports and tests must not depend on a background network request.
            if (!Application.isBatchMode) EditorApplication.delayCall += CheckAutomatically;
        }

        private static void CheckAutomatically() {
            if (!Settings.MasterEnabled) return;
            if (ResolveAutomaticCheckConsent(Application.isBatchMode, () => EditorUtility.DisplayDialog(
                    "Fury++ compatibility checks",
                    "May Fury++ regularly check GitHub for supported VRCFury versions?\n\n"
                    + "The developer manually tests each Fury++ and VRCFury combination before approving it. "
                    + "These checks let you use newly approved VRCFury versions without waiting for a Fury++ update, "
                    + "unless code changes are needed.\n\n"
                    + "Fury++ downloads the approval list when Unity starts and after scripts reload. "
                    + "No avatar data or installed version information is sent. GitHub receives a normal web request, "
                    + "including your IP address.\n\n"
                    + "You can change this choice in Tools > FuryPlusPlus > Settings. "
                    + "It applies to your Unity projects on this computer.",
                    "Allow automatic checks", "No thanks"))) {
                CheckNow();
            }
        }

        internal static bool ResolveAutomaticCheckConsent(bool isBatchMode, Func<bool> ask) {
            if (isBatchMode) return false;
            var consent = Settings.AutomaticCompatibilityChecks;
            if (!consent.HasValue) {
                consent = ask();
                Settings.AutomaticCompatibilityChecks = consent;
            }
            return consent.Value;
        }

        internal static void SetAutomaticChecks(bool enabled) {
            Settings.AutomaticCompatibilityChecks = enabled;
            if (enabled) {
                if (!Application.isBatchMode) CheckNow();
            } else if (IsChecking) {
                CancelRequest();
                Status = "Approval check cancelled. Cached approvals are unchanged.";
            }
        }

        internal static bool IsApproved(string furyPlusPlus, string vrcfury) {
            Initialize();
            return active != null && active.Approves(furyPlusPlus, vrcfury);
        }

        internal static void CheckNow() {
            Initialize();
            if (request != null) return;
            try {
                download = new BoundedDownload();
                request = new UnityWebRequest(CatalogUrl, UnityWebRequest.kHttpVerbGET, download, null) {
                    timeout = 15,
                    redirectLimit = 0
                };
                request.SendWebRequest();
                EditorApplication.update += PollRequest;
                Status = "Checking approvals on GitHub...";
            } catch (Exception e) {
                Status = "Could not check approvals: " + e.Message;
                CancelRequest();
            }
        }

        private static void PollRequest() {
            if (request == null || !request.isDone) return;
            try {
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200) {
                    Status = "Approval check failed. Existing approvals are unchanged. " + request.error;
                    return;
                }
                var json = download.ReadText();
                if (!CompatibilityCatalog.TryParse(json, out var catalog) || !catalog.CanReplace(latest)) {
                    Status = "GitHub returned an invalid or older approval list. Existing approvals are unchanged.";
                    return;
                }
                CompatibilityCache.Write(cachePath, catalog, DateTime.UtcNow);
                latest = catalog;
                var nowApproved = catalog.Approves(PackageIdentity.Version, VrcfuryCompat.LoadedPackageVersion());
                var wasApproved = active != null
                                  && active.Approves(PackageIdentity.Version, VrcfuryCompat.LoadedPackageVersion());
                Status = nowApproved != wasApproved
                    ? (nowApproved ? "This combination is approved. " : "This combination is no longer approved. ")
                      + "Restart Unity or reload scripts to apply the new decision."
                    : "Approval list is up to date. "
                      + (nowApproved ? "This combination is approved." : "This combination has not been approved.");
            } catch (Exception e) {
                Status = "Could not save approvals: " + e.Message;
            } finally {
                CancelRequest();
            }
        }

        private static void CancelRequest() {
            EditorApplication.update -= PollRequest;
            if (request == null) return;
            request.Abort();
            request.Dispose();
            request = null;
            download = null;
        }

        private sealed class BoundedDownload : DownloadHandlerScript {
            private readonly MemoryStream bytes = new MemoryStream();

            internal BoundedDownload() : base(new byte[8192]) { }

            protected override bool ReceiveData(byte[] data, int length) {
                if (data == null || length < 0 || bytes.Length + length > CompatibilityCatalog.MaxBytes) return false;
                bytes.Write(data, 0, length);
                return true;
            }

            internal string ReadText() {
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
        }
    }
}
