using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * When the parameter compressor produces more bool batches than number batches,
     * the int lanes sit idle during the trailing batches even though their 8 bits per lane
     * are already paid for. This module moves the trailing bools into those idle int-lane
     * slots (the compressor's drivers copy bools into int slots losslessly), reducing the
     * number of batches per full sync — faster syncing, and sometimes one fewer index bit.
     *
     * The packing is a deterministic function of the decision tuple VRCFury itself replays
     * onto mobile builds, so both platforms derive the same wire layout as long as
     * FuryPlusPlus (with this module in the same state) is installed for both uploads —
     * that requirement is enforced by the FppSidecar and documented in the README.
     */
    internal sealed class CompressorLanePackingModule : Module<CompressorLanePackingModule> {

        internal override string Id => "compressorLanePacking";
        internal override string DisplayName => "Compressor: pack bools into idle int lanes";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Parameter compressor (sync bits)";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "When the parameter compressor engages (over 256 sync bits), trailing bools ride " +
            "the already-paid-for idle int lanes, cutting batches per sync. If you also upload " +
            "a Quest version of the same avatar, FuryPlusPlus must be installed with the same " +
            "settings for that upload too (the build fails closed if it detects a mismatch).";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorScope.EnsureInstalled(harmony);
        }

        internal override string ReportStats() {
            return CompressorScope.LanePackingStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            var before = CompressorScope.ReportedStockBatches;
            var after = CompressorScope.ReportedPackedBatches;
            return before > after && after > 0
                ? ($"sync rounds {before} → {after} last bake", CompressorScope.LanePackingStats)
                : ((string, string)?)null;
        }
    }
}
