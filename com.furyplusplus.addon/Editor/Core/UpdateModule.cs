using HarmonyLib;

namespace FuryPlusPlus {
    internal sealed class UpdateModule : Module {
        internal static UpdateModule Instance { get; private set; }
        internal UpdateModule() { Instance = this; }
        internal override string Id => "packageUpdates";
        internal override string DisplayName => "Fury++ updates";
        internal override ModuleKind Kind => ModuleKind.Cosmetic;
        internal override CompatTier RequiredTier => CompatTier.PublicSdk;
        internal override string SettingsGroup => "Package updates";
        internal override string Description => "Checks for stable Fury++ releases after you opt in. "
            + "Every installation requires confirmation and keeps a backup. Configure checks at the top of this window.";
        internal override void Install(Harmony harmony, VrcfuryCompat compat) { UpdateService.Initialize(); }
    }
}
