using System;
using System.IO;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public sealed class FppSidecarTests {
        private string root;
        private string fppDirectory;
        private string vrcfuryDirectory;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "FuryPlusPlusTests", Guid.NewGuid().ToString("N"));
            fppDirectory = Path.Combine(root, "fpp");
            vrcfuryDirectory = Path.Combine(root, "vrcfury");
            Directory.CreateDirectory(fppDirectory);
            Directory.CreateDirectory(vrcfuryDirectory);
            FppSidecar.SidecarDirectoryOverride = fppDirectory;
            FppSidecar.VrcfuryDesktopDirectoryOverride = vrcfuryDirectory;
        }

        [TearDown]
        public void TearDown() {
            FppSidecar.SidecarDirectoryOverride = null;
            FppSidecar.VrcfuryDesktopDirectoryOverride = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [TestCase("{\"parameters\":[{\"compressed\":true}]}", true)]
        [TestCase("{ \"parameters\" : [ { \"compressed\" : true } ] }", true)]
        [TestCase("{\"parameters\":[{\"compressed\":false}]}", false)]
        [TestCase("{\"parameters\":[]}", false)]
        public void VrcfuryCompressionParserUsesStructuredJson(string json, bool expected) {
            Assert.That(FppSidecar.TryParseVrcfuryCompression(json, out var compressed), Is.True);
            Assert.That(compressed, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("{}")]
        [TestCase("{\"parameters\":null}")]
        [TestCase("{\"parameters\":[{}]}")]
        [TestCase("{\"parameters\":[{\"compressed\":\"false\"}]}")]
        [TestCase("{\"parameters\":[{\"compressed\":true,\"compressed\":false}]}")]
        [TestCase("not json")]
        public void VrcfuryCompressionParserRejectsUncertainData(string json) {
            Assert.That(FppSidecar.TryParseVrcfuryCompression(json, out _), Is.False);
        }

        [Test]
        public void CorruptFppSidecarFailsClosed() {
            const string blueprintId = "avtr_test";
            File.WriteAllText(Path.Combine(fppDirectory, blueprintId + ".json"), "not json");

            var verified = FppSidecar.VerifyMobileDecision(
                blueprintId, Array.Empty<string>(), out var error);

            Assert.That(verified, Is.False);
            Assert.That(error, Does.Contain("FuryPlusPlus cross-platform sync data"));
        }

        [Test]
        public void CorruptVrcfurySidecarFailsClosed() {
            const string blueprintId = "avtr_test";
            WriteValidFppSidecar(blueprintId);
            File.WriteAllText(Path.Combine(vrcfuryDirectory, blueprintId + ".json"), "not json");

            var verified = FppSidecar.VerifyMobileDecision(
                blueprintId, Array.Empty<string>(), out var error);

            Assert.That(verified, Is.False);
            Assert.That(error, Does.Contain("VRCFury desktop sync data is invalid"));
        }

        [TestCase("../avtr_test")]
        [TestCase("folder/avtr_test")]
        [TestCase("folder\\avtr_test")]
        public void InvalidBlueprintIdFailsClosed(string blueprintId) {
            var verified = FppSidecar.VerifyMobileDecision(
                blueprintId, Array.Empty<string>(), out var error);

            Assert.That(verified, Is.False);
            Assert.That(error, Does.Contain("invalid blueprint ID"));
        }

        private void WriteValidFppSidecar(string blueprintId) {
            File.WriteAllText(
                Path.Combine(fppDirectory, blueprintId + ".json"),
                "{\"algorithmVersion\":1,\"strippedParams\":[],\"narrowedParams\":[]}");
        }
    }
}
