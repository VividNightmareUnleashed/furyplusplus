using NUnit.Framework;
using UnityEngine;

namespace FuryPlusPlus.Tests.Editor {
    public class BakeCacheSnapshotStoreTests {
        private static BakeCacheSnapshotStore.SnapshotMeta MatchingMeta() {
            return new BakeCacheSnapshotStore.SnapshotMeta {
                formatVersion = BakeCacheSnapshotStore.SnapshotFormatVersion,
                hierarchyHash = "H",
                assetsHash = "A",
                configHash = "C",
                generatedHash = "G",
                vrcfuryVersion = "1.1363.0",
                vrcfuryMvid = "MVID",
                chainSeconds = 27.3,
            };
        }

        private static BakeFingerprint.Result MatchingLive() {
            return new BakeFingerprint.Result {
                HierarchyHash = "H",
                AssetsHash = "A",
                ConfigHash = "C",
                GeneratedHash = "G",
            };
        }

        private static bool Compatible(BakeCacheSnapshotStore.SnapshotMeta meta,
            BakeFingerprint.Result live) {
            return BakeCacheSnapshotStore.IsCompatible(meta, live, "1.1363.0", "MVID");
        }

        [Test]
        public void AcceptsExactMatch() {
            Assert.That(Compatible(MatchingMeta(), MatchingLive()), Is.True);
        }

        [Test]
        public void RejectsNullOrEmpty() {
            Assert.That(Compatible(null, MatchingLive()), Is.False);
            Assert.That(BakeCacheSnapshotStore.IsCompatible(
                MatchingMeta(), null, "1.1363.0", "MVID"), Is.False);
            var meta = MatchingMeta();
            meta.hierarchyHash = "";
            Assert.That(Compatible(meta, MatchingLive()), Is.False);
        }

        [Test]
        public void RejectsFormatVersionMismatch() {
            var meta = MatchingMeta();
            meta.formatVersion++;
            Assert.That(Compatible(meta, MatchingLive()), Is.False,
                "a format bump must invalidate every existing snapshot");
        }

        [Test]
        public void RejectsEveryHashMismatch() {
            foreach (var flip in new[] { "hierarchy", "assets", "config", "generated" }) {
                var live = MatchingLive();
                switch (flip) {
                    case "hierarchy": live.HierarchyHash = "X"; break;
                    case "assets": live.AssetsHash = "X"; break;
                    case "config": live.ConfigHash = "X"; break;
                    case "generated": live.GeneratedHash = "X"; break;
                }
                Assert.That(Compatible(MatchingMeta(), live), Is.False, flip);
            }
        }

        [Test]
        public void RejectsVrcfuryDrift() {
            Assert.That(BakeCacheSnapshotStore.IsCompatible(
                MatchingMeta(), MatchingLive(), "1.1364.0", "MVID"), Is.False);
            Assert.That(BakeCacheSnapshotStore.IsCompatible(
                MatchingMeta(), MatchingLive(), "1.1363.0", "OTHER"), Is.False);
        }

        [Test]
        public void SidecarJsonRoundTrips() {
            var json = JsonUtility.ToJson(MatchingMeta());
            var back = JsonUtility.FromJson<BakeCacheSnapshotStore.SnapshotMeta>(json);
            Assert.That(Compatible(back, MatchingLive()), Is.True);
            Assert.That(back.chainSeconds, Is.EqualTo(27.3).Within(1e-9));
        }

        [Test]
        public void SnapshotFolderUsesSanitizedKeys() {
            var key = BakeFingerprint.SanitizeKey("Scene 1__Ava tar!");
            Assert.That(key, Is.EqualTo("Scene_1__Ava_tar_"));
            Assert.That(BakeCacheSnapshotStore.SnapshotFolder(key),
                Is.EqualTo("Packages/com.furyplusplus.bakecache/Snapshots/Scene_1__Ava_tar_"));
        }

        [Test]
        public void TransientPathsAreRecognized() {
            Assert.That(BakeCacheSnapshotStore.IsTransientPath(
                "Packages/com.vrcfury.temp/Builds/Avatar/FX.controller"), Is.True);
            Assert.That(BakeCacheSnapshotStore.IsTransientPath(
                "Packages/nadena.dev.ndmf/__Generated/Avatar/_assets/assets.asset"), Is.True);
            Assert.That(BakeCacheSnapshotStore.IsTransientPath("Assets/Textures/skin.png"), Is.False);
            Assert.That(BakeCacheSnapshotStore.IsTransientPath(
                "Packages/com.furyplusplus.bakecache/Snapshots/x/container-000.asset"), Is.False);
        }
    }
}
