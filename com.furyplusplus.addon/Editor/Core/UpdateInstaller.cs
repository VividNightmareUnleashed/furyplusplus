using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace FuryPlusPlus {
    internal static class UpdateInstaller {
        internal const int MaxArchiveBytes = 16 * 1024 * 1024;
        private const long MaxExpandedBytes = 64 * 1024 * 1024;

        internal static void ValidateLocation(string project, string package) {
            var expected = Path.GetFullPath(Path.Combine(project, "Packages", UpdateRelease.PackageName));
            if (!string.Equals(Path.GetFullPath(package), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This is a linked or external package. Update it through its original installation source.");
            RejectLinks(expected);
            RejectLinks(Path.Combine(project, "Packages", "manifest.json"));
            RejectLinks(Path.Combine(project, "Packages", "vpm-manifest.json"));
            RejectLinkedTree(expected);
            if (Directory.Exists(Path.Combine(expected, ".git")) || File.Exists(Path.Combine(expected, ".git")))
                throw new IOException("This package is a Git checkout. Update it through Git.");
            var manifest = UpdateRelease.ReadObject(File.ReadAllText(Path.Combine(project, "Packages", "manifest.json")));
            if (manifest["dependencies"]?[UpdateRelease.PackageName] != null)
                throw new IOException("Unity Package Manager manages this package. Update it through its original installation source.");
        }

        internal static void ValidateRequirements(JObject installed, JObject next) {
            foreach (var field in new[] { "dependencies", "vpmDependencies", "unity", "unityRelease" }) {
                if (!JToken.DeepEquals(installed[field], next[field]))
                    throw new IOException("This release changes package or Unity requirements. Update Fury++ in Creator Companion.");
            }
        }

        internal static void Extract(byte[] zip, string destination, UpdateRelease release) {
            if (zip.Length > MaxArchiveBytes) throw new InvalidDataException("Release archive is too large.");
            using (var sha = SHA256.Create()) {
                var hash = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "");
                if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release checksum does not match the published checksum.");
            }
            RejectLinks(destination);
            Directory.CreateDirectory(destination);
            if (Directory.GetFileSystemEntries(destination).Length != 0)
                throw new IOException("Update staging directory must be empty.");
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read)) {
                if (archive.Entries.Count > 4096) throw new InvalidDataException("Release contains too many files.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in archive.Entries) {
                    var relative = entry.FullName.TrimEnd('/');
                    if (relative.Length == 0 || relative.Contains("\\") || relative.Contains(":"))
                        throw new InvalidDataException("Invalid archive path.");
                    foreach (var part in relative.Split('/')) {
                        if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")
                            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            throw new InvalidDataException("Invalid archive path.");
                    }
                    if (!seen.Add(relative)) throw new InvalidDataException("Duplicate archive path.");
                    var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (fileType != 0 && fileType != 0x8000 && fileType != 0x4000)
                        throw new InvalidDataException("Links and special files are not allowed in updates.");
                    var target = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Archive path escapes the staging directory.");
                    if (entry.FullName.EndsWith("/")) {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    if (entry.Length > MaxExpandedBytes - expanded) throw new InvalidDataException("Expanded release is too large.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew)) {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                            expanded += count;
                            if (expanded > MaxExpandedBytes) throw new InvalidDataException("Expanded release is too large.");
                            output.Write(buffer, 0, count);
                        }
                    }
                }
            }
            var manifest = UpdateRelease.ReadObject(File.ReadAllText(Path.Combine(destination, "package.json")));
            if ((string)manifest["name"] != UpdateRelease.PackageName || (string)manifest["version"] != release.Version)
                throw new InvalidDataException("Downloaded package identity does not match the release.");
            ValidateRequirements(release.Manifest, manifest);
        }

        // The caller holds Unity's assembly reload and asset editing locks through this transaction.
        internal static string Install(string project, string package, string installedVersion, UpdateRelease release, byte[] zip) {
            ValidateLocation(project, package);
            var installed = UpdateRelease.ReadObject(File.ReadAllText(Path.Combine(package, "package.json")));
            if ((string)installed["name"] != UpdateRelease.PackageName || (string)installed["version"] != installedVersion)
                throw new IOException("The installed package changed. Reload scripts and check again.");
            ValidateRequirements(installed, release.Manifest);
            var vpmPath = Path.Combine(project, "Packages", "vpm-manifest.json");
            var vpm = File.Exists(vpmPath) ? UpdateRelease.ReadObject(File.ReadAllText(vpmPath)) : null;
            var vpmUpdate = PrepareVpmUpdate(vpm, installedVersion, release.Version);
            var work = Path.Combine(project, "Library", "FuryPlusPlus", "Updates", Guid.NewGuid().ToString("N"));
            RejectLinks(work);
            var staged = Path.Combine(work, "new-package");
            var backup = Path.Combine(work, "previous-package");
            var previousManifest = Path.Combine(work, "vpm-manifest.json");
            var pendingManifest = Path.Combine(work, "vpm-manifest.next.json");
            Extract(zip, staged, release);
            if (vpmUpdate != null) {
                File.Copy(vpmPath, previousManifest);
                File.WriteAllText(pendingManifest, vpmUpdate.ToString());
            }
            var moved = false;
            try {
                Directory.Move(package, backup);
                moved = true;
                Directory.Move(staged, package);
                if (vpmUpdate != null) File.Replace(pendingManifest, vpmPath, null);
                return work;
            } catch {
                if (moved) {
                    if (Directory.Exists(package)) Directory.Move(package, Path.Combine(work, "failed-package"));
                    Directory.Move(backup, package);
                }
                throw;
            }
        }

        internal static JObject PrepareVpmUpdate(JObject manifest, string installedVersion, string newVersion) {
            if (manifest == null) return null;
            var direct = manifest["dependencies"] as JObject;
            var locked = manifest["locked"] as JObject;
            var name = UpdateRelease.PackageName;
            if (direct == null || locked == null) throw new InvalidDataException("Invalid VPM manifest. Open Creator Companion to repair it.");
            if (direct[name] == null && locked[name] == null) return null;
            if (!(direct[name] is JObject) || !(locked[name] is JObject package)
                || (string)package["version"] != installedVersion)
                throw new IOException("VPM's package record differs from this installation. Update Fury++ in Creator Companion.");
            foreach (var entry in locked.Properties()) {
                if (entry.Name != name && entry.Value["dependencies"]?[name] != null)
                    throw new IOException("Another package depends on Fury++. Update Fury++ in Creator Companion to check its requirements.");
            }
            var next = (JObject)manifest.DeepClone();
            next["dependencies"][name]["version"] = newVersion;
            next["locked"][name]["version"] = newVersion;
            return next;
        }

        private static void RejectLinks(string path) {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent) {
                if ((current.Exists || File.Exists(current.FullName))
                    && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Updates cannot replace packages through symbolic links or junctions.");
            }
        }

        private static void RejectLinkedTree(string directory) {
            foreach (var path in Directory.GetFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Package contains a symbolic link or junction; update it through its original source.");
                if ((attributes & FileAttributes.Directory) != 0) RejectLinkedTree(path);
            }
        }
    }
}
