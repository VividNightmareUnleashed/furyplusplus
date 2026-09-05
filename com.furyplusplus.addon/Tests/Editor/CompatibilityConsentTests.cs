using NUnit.Framework;

namespace FuryPlusPlus.Tests.Editor {
    public class CompatibilityConsentTests {
        private bool? previousConsent;

        [SetUp]
        public void SetUp() {
            previousConsent = Settings.AutomaticCompatibilityChecks;
            Settings.AutomaticCompatibilityChecks = null;
        }

        [TearDown]
        public void TearDown() {
            Settings.AutomaticCompatibilityChecks = previousConsent;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FirstChoiceIsRememberedWithoutAskingAgain(bool answer) {
            var prompts = 0;
            Assert.That(Settings.AutomaticCompatibilityChecks, Is.Null);
            Assert.That(CompatibilityApprovals.ResolveAutomaticCheckConsent(false, () => {
                prompts++;
                return answer;
            }), Is.EqualTo(answer));
            Assert.That(Settings.AutomaticCompatibilityChecks, Is.EqualTo(answer));
            Assert.That(CompatibilityApprovals.ResolveAutomaticCheckConsent(false, () => {
                prompts++;
                return !answer;
            }), Is.EqualTo(answer));
            Assert.That(prompts, Is.EqualTo(1));
        }

        [TestCase(null)]
        [TestCase(true)]
        [TestCase(false)]
        public void BatchModeNeverPromptsOrAllowsAutomaticChecks(bool? savedConsent) {
            Settings.AutomaticCompatibilityChecks = savedConsent;
            Assert.That(CompatibilityApprovals.ResolveAutomaticCheckConsent(true, () => {
                Assert.Fail("Batch mode must not ask for consent.");
                return true;
            }), Is.False);
            Assert.That(Settings.AutomaticCompatibilityChecks, Is.EqualTo(savedConsent));
        }

        [Test]
        public void WithdrawingConsentPreventsFurtherAutomaticChecks() {
            Settings.AutomaticCompatibilityChecks = true;
            CompatibilityApprovals.SetAutomaticChecks(false);
            Assert.That(CompatibilityApprovals.ResolveAutomaticCheckConsent(false, () => {
                Assert.Fail("A declined choice must not prompt again.");
                return true;
            }), Is.False);
        }
    }
}
