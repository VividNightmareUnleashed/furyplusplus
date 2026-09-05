using System;
using System.IO;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class CompatibilityCatalogTests {
        private const string Approved =
            "{\"schemaVersion\":1,\"revision\":2,\"approved\":[" +
            "{\"furyPlusPlus\":\"1.2.5\",\"vrcfury\":[\"1.1427.0\",\"1.1428.0\"]}]}";

        [Test]
        public void ApprovalRequiresBothExactVersions() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var catalog), Is.True);
            Assert.That(catalog.Approves("1.2.5", "1.1427.0"), Is.True);
            Assert.That(catalog.Approves("1.2.5", "1.1428.0"), Is.True);
            Assert.That(catalog.Approves("1.2.6", "1.1428.0"), Is.False);
            Assert.That(catalog.Approves("1.2.5", "1.1429.0"), Is.False);
            Assert.That(catalog.Approves("1.2.5", "1.1427.0-beta.1"), Is.False);
            Assert.That(catalog.Approves("unknown", "1.1427.0"), Is.False);
            Assert.That(catalog.Approves(null, null), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("{")]
        [TestCase("{}")]
        [TestCase("{\"schemaVersion\":2,\"revision\":1,\"approved\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"approved\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"revision\":1}")]
        public void InvalidDocumentsNeverApprove(string json) {
            Assert.That(CompatibilityCatalog.TryParse(json, out var catalog), Is.False);
            Assert.That(catalog, Is.Null);
        }

        [TestCase("*")]
        [TestCase(">=1.1427.0")]
        [TestCase("1.1427")]
        [TestCase("01.1427.0")]
        [TestCase("1.1427.0 ")]
        [TestCase("1.1427.0.0")]
        [TestCase("1.1427.0-beta.1")]
        public void CatalogDoesNotAcceptRangesOrNonReleaseVersions(string version) {
            var json = Approved.Replace("1.1427.0", version);
            Assert.That(CompatibilityCatalog.TryParse(json, out _), Is.False);
        }

        [Test]
        public void DuplicateVersionsAndAddonRowsAreRejected() {
            Assert.That(CompatibilityCatalog.TryParse(Approved.Replace("1.1428.0", "1.1427.0"), out _), Is.False);
            const string duplicated = "{\"schemaVersion\":1,\"revision\":1,\"approved\":[" +
                                      "{\"furyPlusPlus\":\"1.2.5\",\"vrcfury\":[]}," +
                                      "{\"furyPlusPlus\":\"1.2.5\",\"vrcfury\":[\"1.1427.0\"]}]}";
            Assert.That(CompatibilityCatalog.TryParse(duplicated, out _), Is.False);
        }

        [Test]
        public void OversizedDocumentIsRejected() {
            Assert.That(CompatibilityCatalog.TryParse(new string(' ', CompatibilityCatalog.MaxBytes) + Approved,
                out _), Is.False);
        }

        [Test]
        public void NewCatalogCanRemovePreviouslyApprovedPairs() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var previous), Is.True);
            Assert.That(CompatibilityCatalog.TryParse(
                "{\"schemaVersion\":1,\"revision\":3,\"approved\":[]}", out var next), Is.True);
            Assert.That(next.CanReplace(previous), Is.True);
            Assert.That(next.Approves("1.2.5", "1.1427.0"), Is.False);
            // A session holding the previous snapshot does not change halfway through a bake.
            Assert.That(previous.Approves("1.2.5", "1.1427.0"), Is.True);
        }

        [Test]
        public void OlderOrConflictingRevisionsCannotReplaceTheCache() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var current), Is.True);
            Assert.That(CompatibilityCatalog.TryParse(Approved.Replace("\"revision\":2", "\"revision\":1"),
                out var older), Is.True);
            Assert.That(CompatibilityCatalog.TryParse(Approved.Replace("1.1428.0", "1.1429.0"),
                out var conflict), Is.True);
            Assert.That(older.CanReplace(current), Is.False);
            Assert.That(conflict.CanReplace(current), Is.False);
            Assert.That(current.CanReplace(current), Is.True);
            Assert.That(current.CanReplace(null), Is.True);
        }

        [Test]
        public void CachedApprovalSurvivesAnOfflineRestartWithinThirtyDays() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var catalog), Is.True);
            var fetched = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            var cache = CompatibilityCache.Serialize(catalog, fetched);
            Assert.That(CompatibilityCache.TryRead(cache, fetched.AddDays(30), out var cached, out var fresh), Is.True);
            Assert.That(fresh, Is.True);
            Assert.That(cached.Approves("1.2.5", "1.1427.0"), Is.True);
        }

        [Test]
        public void ExpiredCacheRetainsRevisionButCannotAuthorizeACombination() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var catalog), Is.True);
            var fetched = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            var cache = CompatibilityCache.Serialize(catalog, fetched);
            Assert.That(CompatibilityCache.TryRead(cache, fetched.AddDays(30).AddTicks(1),
                out var cached, out var fresh), Is.True);
            Assert.That(fresh, Is.False);
            Assert.That(cached.Revision, Is.EqualTo(2));
        }

        [Test]
        public void FutureAndCorruptCacheRecordsAreRejected() {
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var catalog), Is.True);
            var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            var future = CompatibilityCache.Serialize(catalog, now.AddDays(1));
            Assert.That(CompatibilityCache.TryRead(future, now, out _, out var fresh), Is.False);
            Assert.That(fresh, Is.False);
            Assert.That(CompatibilityCache.TryRead("{}", now, out _, out _), Is.False);
            Assert.That(CompatibilityCache.TryRead("not JSON", now, out _, out _), Is.False);
        }

        [Test]
        public void CacheWriteReplacesThePreviousCatalog() {
            var directory = Path.Combine(Path.GetTempPath(), "FuryPlusPlus-compatibility-test-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "cache.json");
            var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
            Assert.That(CompatibilityCatalog.TryParse(Approved, out var initial), Is.True);
            Assert.That(CompatibilityCatalog.TryParse(
                "{\"schemaVersion\":1,\"revision\":3,\"approved\":[]}", out var revoked), Is.True);
            try {
                CompatibilityCache.Write(path, initial, now);
                CompatibilityCache.Write(path, revoked, now.AddMinutes(1));
                Assert.That(CompatibilityCache.TryRead(File.ReadAllText(path), now.AddMinutes(2),
                    out var cached, out var fresh), Is.True);
                Assert.That(fresh, Is.True);
                Assert.That(cached.Revision, Is.EqualTo(3));
                Assert.That(cached.Approves("1.2.5", "1.1427.0"), Is.False);
                Assert.That(File.Exists(path + ".tmp"), Is.False);
            } finally {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
