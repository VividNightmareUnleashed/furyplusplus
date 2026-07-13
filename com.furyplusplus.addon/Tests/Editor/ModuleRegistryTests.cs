using System.Linq;
using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class ModuleRegistryTests {
        [Test]
        public void ModuleIdsAreUnique() {
            var ids = ModuleRegistry.All.Select(module => module.Id).ToList();
            Assert.That(ids, Is.Unique);
        }

        [Test]
        public void ModuleIdsAreValidPrefKeyStems() {
            foreach (var module in ModuleRegistry.All) {
                Assert.That(module.Id, Is.Not.Null.And.Not.Empty);
                Assert.That(module.Id, Does.Not.Contain(" "), $"{module.Id}: no spaces");
                Assert.That(module.Id, Does.Not.Contain("."), $"{module.Id}: no dots (dots separate option suffixes)");
            }
        }

        [Test]
        public void ModuleDisplayNamesAreSet() {
            foreach (var module in ModuleRegistry.All) {
                Assert.That(module.DisplayName, Is.Not.Null.And.Not.Empty, module.Id);
            }
        }

        [Test]
        public void OptionSuffixesAreUniquePerModule() {
            foreach (var module in ModuleRegistry.All) {
                var suffixes = module.Options.Select(option => option.Suffix).ToList();
                Assert.That(suffixes, Is.Unique, module.Id);
                foreach (var suffix in suffixes) {
                    Assert.That(suffix, Is.Not.Null.And.Not.Empty, module.Id);
                    Assert.That(suffix, Does.Not.Contain(" "), $"{module.Id}.{suffix}");
                    Assert.That(suffix, Does.Not.Contain("."), $"{module.Id}.{suffix}");
                }
            }
        }

        [Test]
        public void QualityModulesRequireExactVersion() {
            foreach (var module in ModuleRegistry.All) {
                if (module.Kind == ModuleKind.Quality) {
                    Assert.That(module.RequiredTier, Is.EqualTo(CompatTier.ExactVersion),
                        $"{module.Id}: Quality modules change VRCFury's output and must be version-pinned");
                }
            }
        }
    }
}
