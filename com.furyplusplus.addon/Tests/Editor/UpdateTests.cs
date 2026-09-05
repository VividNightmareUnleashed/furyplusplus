using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class UpdateTests {
        private string directory;

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "FuryPlusPlus-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static JObject Manifest(string version) {
            return new JObject {
                ["name"] = UpdateRelease.PackageName, ["version"] = version, ["unity"] = "2022.3",
                ["vpmDependencies"] = new JObject { ["com.vrcfury.vrcfury"] = ">=1.1427.0 <2.0.0" }
            };
        }

        private static string Listing(byte[] zip, string version = "1.2.6") {
            var manifest = Manifest(version);
            using (var sha = SHA256.Create()) manifest["zipSHA256"] = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "");
            manifest["url"] = UpdateRelease.RepositoryUrl + "/releases/download/v" + version
                + "/" + UpdateRelease.PackageName + "-" + version + ".zip";
            return new JObject { ["packages"] = new JObject { [UpdateRelease.PackageName] =
                new JObject { ["versions"] = new JObject { [version] = manifest } } } }.ToString();
        }

        private static byte[] Archive(string extraPath = "Editor/new.cs", bool symlink = false, string version = "1.2.6") {
            using (var stream = new MemoryStream()) {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
                    using (var writer = new StreamWriter(archive.CreateEntry("package.json").Open())) writer.Write(Manifest(version).ToString());
                    var extra = archive.CreateEntry(extraPath);
                    if (symlink) extra.ExternalAttributes = 0xA000 << 16;
                    using (var writer = new StreamWriter(extra.Open())) writer.Write("content");
                }
                return stream.ToArray();
            }
        }

        [Test]
        public void SelectsNewerStableReleasesWithoutDowngrading() {
            var listing = Listing(Archive());
            Assert.That(UpdateRelease.FindNewer(listing, "1.2.5").Version, Is.EqualTo("1.2.6"));
            Assert.That(UpdateRelease.FindNewer(listing, "1.2.6"), Is.Null);
            Assert.That(UpdateRelease.FindNewer(listing, "1.3.0"), Is.Null);
            Assert.That(UpdateRelease.FindNewer(Listing(Archive(), "1.2.7-beta.1"), "1.2.5"), Is.Null);
        }

        [Test]
        public void RejectsUnexpectedAssetUrlsAndInvalidChecksums() {
            var listing = Listing(Archive());
            Assert.Throws<InvalidDataException>(() => UpdateRelease.FindNewer(
                listing.Replace(UpdateRelease.RepositoryUrl, "https://example.com/another/repo"), "1.2.5"));
            var document = UpdateRelease.ReadObject(listing);
            document["packages"][UpdateRelease.PackageName]["versions"]["1.2.6"]["zipSHA256"] = "invalid";
            Assert.Throws<InvalidDataException>(() => UpdateRelease.FindNewer(document.ToString(), "1.2.5"));
        }

        [TestCase("https://release-assets.githubusercontent.com/release")]
        [TestCase("https://objects.githubusercontent.com/release")]
        public void AllowsGitHubAssetRedirects(string url) {
            Assert.That(UpdateDownload.IsAssetHost(new Uri(url)), Is.True);
        }

        [TestCase("http://github.com/release")]
        [TestCase("https://github.com.example.com/release")]
        [TestCase("https://github.com:444/release")]
        [TestCase("https://user@github.com/release")]
        public void RejectsUntrustedAssetRedirects(string url) {
            Assert.That(UpdateDownload.IsAssetHost(new Uri(url)), Is.False);
        }

        [TestCase("../escaped.cs")]
        [TestCase("/absolute.cs")]
        [TestCase("Editor/../../escaped.cs")]
        [TestCase("Editor\\escaped.cs")]
        [TestCase("C:/escaped.cs")]
        [TestCase("PACKAGE.JSON")]
        public void ArchiveCannotEscapeOrOverwriteAnotherEntry(string path) {
            var zip = Archive(path);
            var release = UpdateRelease.FindNewer(Listing(zip), "1.2.5");
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(zip, Path.Combine(directory, "stage"), release));
            Assert.That(File.Exists(Path.Combine(directory, "escaped.cs")), Is.False);
        }

        [Test]
        public void RejectsSymlinksAndMismatchedPackageIdentity() {
            var linked = Archive(symlink: true);
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(linked, Path.Combine(directory, "linked"),
                UpdateRelease.FindNewer(Listing(linked), "1.2.5")));
            var wrongVersion = Archive(version: "1.2.7");
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(wrongVersion, Path.Combine(directory, "wrong"),
                UpdateRelease.FindNewer(Listing(wrongVersion), "1.2.5")));
        }

        [Test]
        public void ChecksumFailureDoesNotCreateStagingFiles() {
            var zip = Archive();
            var release = UpdateRelease.FindNewer(Listing(zip), "1.2.5");
            zip[0] ^= 1;
            var stage = Path.Combine(directory, "stage");
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(zip, stage, release));
            Assert.That(Directory.Exists(stage), Is.False);
        }

        [Test]
        public void ChangedDependenciesRequireCreatorCompanion() {
            var installed = Manifest("1.2.5");
            var next = Manifest("1.2.6");
            next["vpmDependencies"]["com.vrcfury.vrcfury"] = ">=2.0.0";
            Assert.Throws<IOException>(() => UpdateInstaller.ValidateRequirements(installed, next));
        }

        private string PreparePackage(bool vpm = true) {
            var package = Path.Combine(directory, "Packages", UpdateRelease.PackageName);
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), Manifest("1.2.5").ToString());
            File.WriteAllText(Path.Combine(package, "obsolete.cs"), "old content");
            File.WriteAllText(Path.Combine(directory, "Packages", "manifest.json"), "{\"dependencies\":{}}");
            if (vpm) {
                var manifest = new JObject {
                    ["dependencies"] = new JObject { [UpdateRelease.PackageName] = new JObject { ["version"] = "1.2.5" } },
                    ["locked"] = new JObject { [UpdateRelease.PackageName] = new JObject { ["version"] = "1.2.5" },
                        ["another.package"] = new JObject { ["version"] = "3.0.0", ["dependencies"] = new JObject() } },
                    ["custom"] = "preserve me"
                };
                File.WriteAllText(Path.Combine(directory, "Packages", "vpm-manifest.json"), manifest.ToString());
            }
            return package;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InstallReplacesObsoleteFilesAndKeepsBackup(bool vpm) {
            var package = PreparePackage(vpm);
            var zip = Archive();
            var backup = UpdateInstaller.Install(directory, package, "1.2.5", UpdateRelease.FindNewer(Listing(zip), "1.2.5"), zip);
            Assert.That(File.Exists(Path.Combine(package, "obsolete.cs")), Is.False);
            Assert.That(File.Exists(Path.Combine(package, "Editor", "new.cs")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(backup, "previous-package", "obsolete.cs")), Is.EqualTo("old content"));
            if (vpm) {
                var manifest = UpdateRelease.ReadObject(File.ReadAllText(Path.Combine(directory, "Packages", "vpm-manifest.json")));
                Assert.That((string)manifest["dependencies"][UpdateRelease.PackageName]["version"], Is.EqualTo("1.2.6"));
                Assert.That((string)manifest["locked"][UpdateRelease.PackageName]["version"], Is.EqualTo("1.2.6"));
                Assert.That((string)manifest["locked"]["another.package"]["version"], Is.EqualTo("3.0.0"));
                Assert.That((string)manifest["custom"], Is.EqualTo("preserve me"));
                Assert.That(File.Exists(Path.Combine(backup, "vpm-manifest.json")), Is.True);
            }
        }

        [Test]
        public void ManifestWriteFailureRestoresTheOldPackage() {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Requires Windows file sharing enforcement.");
            var package = PreparePackage();
            var manifestPath = Path.Combine(directory, "Packages", "vpm-manifest.json");
            var original = File.ReadAllText(manifestPath);
            var zip = Archive();
            using (var locked = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Assert.Throws<IOException>(() => UpdateInstaller.Install(directory, package, "1.2.5",
                    UpdateRelease.FindNewer(Listing(zip), "1.2.5"), zip));
            }
            Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(original));
            Assert.That(File.ReadAllText(Path.Combine(package, "obsolete.cs")), Is.EqualTo("old content"));
        }

        [Test]
        public void RejectsStaleVpmRecordsAndOtherPackageConstraints() {
            var package = PreparePackage();
            var path = Path.Combine(directory, "Packages", "vpm-manifest.json");
            var manifest = UpdateRelease.ReadObject(File.ReadAllText(path));
            Assert.Throws<IOException>(() => UpdateInstaller.PrepareVpmUpdate(manifest, "1.2.4", "1.2.6"));
            manifest["locked"]["another.package"]["dependencies"][UpdateRelease.PackageName] = "1.2.5";
            Assert.Throws<IOException>(() => UpdateInstaller.PrepareVpmUpdate(manifest, "1.2.5", "1.2.6"));
            Assert.Throws<IOException>(() => UpdateInstaller.ValidateLocation(directory, Path.Combine(directory, "external")));
            File.WriteAllText(Path.Combine(package, ".git"), "gitdir: somewhere");
            Assert.Throws<IOException>(() => UpdateInstaller.ValidateLocation(directory, package));
        }
    }
}
