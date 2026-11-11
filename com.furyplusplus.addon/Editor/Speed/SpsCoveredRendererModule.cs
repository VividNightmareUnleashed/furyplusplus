using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. SpsUpgrader's automatic DPS/TPS discovery used to bake a renderer's
     * complete mesh to prove its size before AddPlug checked whether a plug already covered
     * that object; this module skipped the bake for renderers above or below an existing or
     * newly unbaked plug. VRCFury 1.1443.0 split plug upgrading into SpsPlugUpgrader, whose
     * Discover() runs the same AlreadyExistsAboveOrBelow check (against existing plug owners,
     * their renderers and plugs added earlier in the run) before GetAutoWorldSize. There is
     * no covered-renderer probe left to skip, so the patch code is removed and the toggle
     * stays (struck through).
     */
    internal sealed class SpsCoveredRendererModule : Module<SpsCoveredRendererModule> {

        internal override string Id => "spsCoveredRenderer";
        internal override string DisplayName => "Covered SPS mesh probe skip";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "SPS";
        internal override string Description =>
            "Superseded by VRCFury's own covered-plug check before the mesh-size probe (1.1443.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1443.0",
            "SpsPlugUpgrader.Discover skips renderers already covered by a plug before baking the mesh to size it.",
            "https://github.com/VRCFury/VRCFury/commit/fa9f9573a254472e58b589a8c9b9747229be9ffe");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
