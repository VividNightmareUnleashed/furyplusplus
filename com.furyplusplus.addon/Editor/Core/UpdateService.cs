using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase.Editor;

namespace FuryPlusPlus {
    internal static class UpdateService {
        private const string LastCheckKey = Settings.KeyPrefix + "updates.lastCheck";
        private const string ListingKey = Settings.KeyPrefix + "updates.listing";
        private const string NoticeKey = Settings.KeyPrefix + "updates.notice";
        private static bool initialized;
        private static double nextTick;
        private static CancellationTokenSource request;
        internal static UpdateRelease Available { get; private set; }
        internal static bool IsBusy => request != null;
        internal static string Status { get; private set; } = "Fury++ updates have not been checked.";

        internal static bool EditorIsIdle => !Application.isBatchMode && !EditorApplication.isCompiling
            && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode
            && !BuildPipeline.isBuildingPlayer && BuildPhaseHooks.CurrentAvatarRoot == null
            && (VRCSdkControlPanel.window == null || VRCSdkControlPanel.window.PanelState == SdkPanelState.Idle);

        internal static void Initialize() {
            if (initialized) return;
            initialized = true;
            if (Application.isBatchMode) return;
            var cached = SessionState.GetString(ListingKey, "");
            if (cached.Length > 0) {
                try {
                    Available = UpdateRelease.FindNewer(cached, PackageIdentity.Version);
                    Status = Available == null ? "No newer stable Fury++ release was found in the last check."
                        : "Fury++ " + Available.Version + " is available.";
                } catch { SessionState.EraseString(ListingKey); }
            }
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
            EditorApplication.update += Tick;
            EditorApplication.delayCall += Tick;
        }

        private static void Tick() {
            if (EditorApplication.timeSinceStartup < nextTick) return;
            nextTick = EditorApplication.timeSinceStartup + 60;
            if (!EditorIsIdle || IsBusy || UpdateModule.Instance == null || !UpdateModule.Instance.Enabled) return;
            if (!Settings.AutomaticUpdateChecks.HasValue) {
                Settings.AutomaticUpdateChecks = EditorUtility.DisplayDialog("Fury++ update checks",
                    "May Fury++ automatically check for new Fury++ releases?\n\n"
                    + "Fury++ checks the public GitHub Pages package listing when Unity starts and once a day while it stays open. "
                    + "GitHub receives your IP address, but no avatar data or installed version information.\n\n"
                    + "Downloading and installing a release package always requires your confirmation. You can change this choice in "
                    + "Tools > FuryPlusPlus > Settings. This is separate from VRCFury compatibility checks "
                    + "and applies to your Unity projects on this computer.", "Allow update checks", "No thanks");
            }
            if (Settings.AutomaticUpdateChecks != true) return;
            if (Available != null && SessionState.GetString(NoticeKey, "") != Available.Version) {
                SessionState.SetString(NoticeKey, Available.Version);
                if (EditorUtility.DisplayDialog("Fury++ update available", "Fury++ " + Available.Version
                    + " is available. Review the release before installing it.", "Review update", "Later")) SettingsWindow.Open();
            }
            if (!long.TryParse(SessionState.GetString(LastCheckKey, ""), out var ticks)
                || DateTime.UtcNow.Ticks < ticks || DateTime.UtcNow.Ticks - ticks >= TimeSpan.FromDays(1).Ticks) CheckNow();
        }

        internal static void SetAutomaticChecks(bool enabled) {
            Settings.AutomaticUpdateChecks = enabled;
            if (enabled) CheckNow();
            else Cancel();
        }

        internal static async void CheckNow() {
            Initialize();
            if (IsBusy || !EditorIsIdle) return;
            var pending = new CancellationTokenSource();
            request = pending;
            Status = "Checking Fury++ releases...";
            SessionState.SetString(LastCheckKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            try {
                var bytes = await UpdateDownload.Get(UpdateRelease.ListingUrl, 1024 * 1024, false, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                var json = new UTF8Encoding(false, true).GetString(bytes);
                Available = UpdateRelease.FindNewer(json, PackageIdentity.Version);
                SessionState.SetString(ListingKey, json);
                Status = Available == null ? "No newer stable Fury++ release was found."
                    : "Fury++ " + Available.Version + " is available.";
            } catch (OperationCanceledException) {
                Status = "Update check cancelled or timed out. Check again when ready.";
            } catch (Exception e) {
                Status = "Could not check Fury++ updates: " + e.Message;
            } finally {
                request = null;
                pending.Dispose();
            }
        }

        internal static async void InstallAvailable() {
            if (IsBusy || Available == null || !EditorIsIdle) return;
            var release = Available;
            var pending = new CancellationTokenSource();
            request = pending;
            try {
                var project = Path.GetDirectoryName(Application.dataPath);
                var package = PackageInfo.FindForAssembly(typeof(UpdateService).Assembly)?.resolvedPath;
                if (string.IsNullOrEmpty(package)) throw new IOException("Cannot locate the installed package.");
                UpdateInstaller.ValidateLocation(project, package);
                var installed = UpdateRelease.ReadObject(File.ReadAllText(Path.Combine(package, "package.json")));
                UpdateInstaller.ValidateRequirements(installed, release.Manifest);
                if (!EditorUtility.DisplayDialog("Install Fury++ " + release.Version + "?",
                    "Update Fury++ " + PackageIdentity.Version + " to " + release.Version + " in this project?\n\n"
                    + "Fury++ will download the release and compatibility list from GitHub, verify the checksum and "
                    + "require approval for your installed VRCFury version. No avatar data is sent.\n\n"
                    + "The current package and VPM record will be backed up under Library/FuryPlusPlus/Updates. "
                    + "Package files will be replaced, including local edits. "
                    + "Unity will reload scripts after installation. VRCFury and the VRChat SDK will not be updated.",
                    "Download and install", "Cancel")) return;
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                Status = "Checking release compatibility...";
                var catalogBytes = await UpdateDownload.Get(CompatibilityApprovals.CatalogUrl,
                    CompatibilityCatalog.MaxBytes, false, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                if (!CompatibilityCatalog.TryParse(new UTF8Encoding(false, true).GetString(catalogBytes), out var catalog)
                    || !catalog.Approves(release.Version, VrcfuryCompat.LoadedPackageVersion()))
                    throw new IOException("This Fury++ release is not approved for your installed VRCFury version. Nothing was installed.");
                CompatibilityApprovals.SaveForNextReload(catalog);
                Status = "Downloading Fury++ " + release.Version + "...";
                var zip = await UpdateDownload.Get(release.ZipUrl, UpdateInstaller.MaxArchiveBytes, true, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                if (!EditorIsIdle) throw new IOException("Unity is busy. Retry the update after the build, import or play session finishes.");
                EditorApplication.LockReloadAssemblies();
                try {
                    AssetDatabase.StartAssetEditing();
                    try {
                        var backup = UpdateInstaller.Install(project, package, PackageIdentity.Version, release, zip);
                        Status = "Fury++ " + release.Version + " installed. Backup: " + backup;
                        Log.Info(Status);
                    } finally { AssetDatabase.StopAssetEditing(); }
                } finally { EditorApplication.UnlockReloadAssemblies(); }
                Available = null;
                AssetDatabase.Refresh();
            } catch (OperationCanceledException) {
                Status = "Update cancelled or timed out. Nothing was installed.";
            } catch (Exception e) {
                Status = "Could not install the Fury++ update: " + e.Message;
                Log.Warn(Status);
            } finally {
                request = null;
                pending.Dispose();
            }
        }

        private static void Cancel() { request?.Cancel(); }
    }
}
