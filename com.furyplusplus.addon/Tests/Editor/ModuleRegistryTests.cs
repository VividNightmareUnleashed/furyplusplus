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
        public void DescribeStatesIncludesOptionStates() {
            var module = ModuleRegistry.All.First(candidate => candidate.Options.Count > 0);
            var option = module.Options[0];
            var key = Settings.OptionKey(module, option);
            var had = UnityEditor.EditorPrefs.HasKey(key);
            var previous = had && Settings.IsOptionEnabled(module, option);
            try {
                Settings.SetOptionEnabled(module, option, true);
                var whenOn = ModuleRegistry.DescribeStates();
                Settings.SetOptionEnabled(module, option, false);
                var whenOff = ModuleRegistry.DescribeStates();
                Assert.That(whenOn, Does.Contain($"[{option.Suffix}=on"),
                    "option states must join the summary (it feeds the bake-cache config hash)");
                Assert.That(whenOff, Does.Contain($"[{option.Suffix}=off"));
                Assert.That(whenOn, Is.Not.EqualTo(whenOff));
            } finally {
                if (had) Settings.SetOptionEnabled(module, option, previous);
                else UnityEditor.EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void OutputConfigIgnoresCosmeticModules() {
            // Toggling a cosmetic module must never invalidate bake-cache snapshots.
            var cosmetic = ModuleRegistry.ByKind(ModuleKind.Cosmetic).First();
            var key = Settings.ModuleKey(cosmetic);
            var had = UnityEditor.EditorPrefs.HasKey(key);
            var previous = had && UnityEditor.EditorPrefs.GetBool(key, cosmetic.DefaultEnabled);
            try {
                Settings.SetModuleEnabled(cosmetic, true);
                var whenOn = ModuleRegistry.DescribeOutputConfig();
                Settings.SetModuleEnabled(cosmetic, false);
                var whenOff = ModuleRegistry.DescribeOutputConfig();
                Assert.That(whenOn, Is.EqualTo(whenOff),
                    "cosmetic toggles must not churn the bake-cache config key");
                Assert.That(whenOn, Does.Not.Contain(cosmetic.Id));
            } finally {
                if (had) Settings.SetModuleEnabled(cosmetic, previous);
                else UnityEditor.EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void OutputConfigTracksListOptions() {
            // The compressor precision list changes bake output, so it must feed the key.
            var module = ModuleRegistry.All.First(candidate => candidate.ListOptions.Count > 0);
            var option = module.ListOptions[0];
            var key = option.KeyFor(module);
            var had = UnityEditor.EditorPrefs.HasKey(key);
            var previous = Settings.GetListOption(module, option);
            var moduleKey = Settings.ModuleKey(module);
            var hadModule = UnityEditor.EditorPrefs.HasKey(moduleKey);
            var moduleWasOn = hadModule && UnityEditor.EditorPrefs.GetBool(moduleKey, module.DefaultEnabled);
            if (!ModuleRegistry.IsActive(module)) {
                Assert.Ignore("module not installed (VRCFury absent or incompatible)");
            }
            try {
                Settings.SetModuleEnabled(module, true);
                Settings.SetListOption(module, option, "TestParam/*");
                var withList = ModuleRegistry.DescribeOutputConfig();
                Settings.SetListOption(module, option, "");
                var withoutList = ModuleRegistry.DescribeOutputConfig();
                Assert.That(withList, Is.Not.EqualTo(withoutList),
                    "list-setting edits change bake output and must invalidate the config key");
                Assert.That(withList, Does.Contain("TestParam/*"));
            } finally {
                if (had) Settings.SetListOption(module, option, previous);
                else UnityEditor.EditorPrefs.DeleteKey(key);
                if (hadModule) Settings.SetModuleEnabled(module, moduleWasOn);
                else UnityEditor.EditorPrefs.DeleteKey(moduleKey);
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
