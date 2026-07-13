using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class SpsProbeResultMapTests {
        [Test]
        public void RoundTripPreservesEntries() {
            var map = new SpsProbeResultMap(8);
            map.Set("aa", true);
            map.Set("bb", false);

            var restored = SpsProbeResultMap.Deserialize(map.Serialize(), 8);

            Assert.That(restored.Count, Is.EqualTo(2));
            Assert.That(restored.TryGet("aa", out var first), Is.True);
            Assert.That(first, Is.True);
            Assert.That(restored.TryGet("bb", out var second), Is.True);
            Assert.That(second, Is.False);
        }

        [Test]
        public void EvictsLeastRecentlyUsedEntryAtCapacity() {
            var map = new SpsProbeResultMap(2);
            map.Set("aa", true);
            map.Set("bb", true);
            map.Set("cc", true);

            Assert.That(map.Count, Is.EqualTo(2));
            Assert.That(map.TryGet("aa", out _), Is.False);
            Assert.That(map.TryGet("bb", out _), Is.True);
            Assert.That(map.TryGet("cc", out _), Is.True);
        }

        [Test]
        public void ReadRefreshesRecency() {
            var map = new SpsProbeResultMap(2);
            map.Set("aa", true);
            map.Set("bb", true);
            map.TryGet("aa", out _);
            map.Set("cc", true);

            Assert.That(map.TryGet("bb", out _), Is.False);
            Assert.That(map.TryGet("aa", out _), Is.True);
        }

        [Test]
        public void SerializesLeastRecentFirst() {
            var map = new SpsProbeResultMap(4);
            map.Set("aa", true);
            map.Set("bb", false);
            map.TryGet("aa", out _);

            Assert.That(map.Serialize(), Is.EqualTo("bb=0;aa=1;"));
        }

        [Test]
        public void DeserializeSkipsMalformedEntries() {
            var map = SpsProbeResultMap.Deserialize("aa=1;;broken;bb=;=1;cc=2;dd=0;", 8);

            Assert.That(map.Count, Is.EqualTo(2));
            Assert.That(map.TryGet("aa", out var kept), Is.True);
            Assert.That(kept, Is.True);
            Assert.That(map.TryGet("dd", out var last), Is.True);
            Assert.That(last, Is.False);
        }

        [Test]
        public void DeserializeTrimsToCapacityKeepingMostRecent() {
            var map = SpsProbeResultMap.Deserialize("aa=1;bb=1;cc=1;", 2);

            Assert.That(map.Count, Is.EqualTo(2));
            Assert.That(map.TryGet("aa", out _), Is.False);
            Assert.That(map.TryGet("bb", out _), Is.True);
            Assert.That(map.TryGet("cc", out _), Is.True);
        }

        [Test]
        public void DirtyOnlyWhenContentOrOrderChanges() {
            var map = SpsProbeResultMap.Deserialize("aa=1;bb=0;", 4);
            Assert.That(map.Dirty, Is.False);

            map.TryGet("bb", out _);
            Assert.That(map.Dirty, Is.False, "reading the most recent key should not dirty the map");

            map.Set("bb", false);
            Assert.That(map.Dirty, Is.False, "rewriting an identical value should not dirty the map");

            map.TryGet("aa", out _);
            Assert.That(map.Dirty, Is.True, "recency changes must persist for LRU eviction to hold");

            map.Serialize();
            Assert.That(map.Dirty, Is.False);

            map.Set("cc", true);
            Assert.That(map.Dirty, Is.True);
        }
    }
}
