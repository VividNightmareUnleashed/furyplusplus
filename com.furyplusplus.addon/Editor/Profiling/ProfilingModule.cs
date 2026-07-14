using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * Always-on bake profiler: exact per-action durations on every VRCFury build, plus an
     * opt-in detailed tier (~40 per-method trampolines on VRCFury's hottest internals).
     * Works on any VRCFury version (Profiling tier).
     */
    internal sealed class ProfilingModule : Module<ProfilingModule> {

        internal override string Id => "profiling";
        internal override string DisplayName => "Bake profiler";
        internal override ModuleKind Kind => ModuleKind.Cosmetic;
        internal override string SettingsGroup => "Editor visuals";
        internal override CompatTier RequiredTier => CompatTier.Profiling;
        internal override string Description =>
            "Logs a per-phase timing report after every VRCFury bake. " +
            "Detailed profiling (below) additionally times VRCFury's hottest internals.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ProfilePatches.Install(harmony, compat);
        }
    }
}
