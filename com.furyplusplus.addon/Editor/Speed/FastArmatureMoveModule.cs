using HarmonyLib;

namespace FuryPlusPlus {
    /**
     * SUPERSEDED. ObjectMoveService.Move used to rebuild the complete humanoid immovable-bone
     * set on every call, from VRCFArmatureUtils.GetAllBones plus an ancestor walk per bone.
     * Armature Link performs thousands of moves against one avatar, so this built that
     * invariant set once per link instead. VRCFury 1.1372.0 added VRCFArmatureCache, which
     * captures nonEyeBoneParents once and answers Move's check with a single HashSet lookup
     * (IsNonEyeBoneParent) — the same optimization, natively. The per-call rebuild this
     * patched no longer exists; the patch code is removed and the toggle stays (struck
     * through) pointing at the upstream commit.
     */
    internal sealed class FastArmatureMoveModule : Module<FastArmatureMoveModule> {

        internal override string Id => "fastArmatureMove";
        internal override string DisplayName => "Fast Armature Link moves";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Armature & links";
        internal override string Description =>
            "Superseded by VRCFury's native humanoid bone-parent cache (1.1372.0).";

        internal override NativeEquivalent? Superseded => new NativeEquivalent(
            "1.1372.0",
            "VRCFArmatureCache captures the non-eye bone parents once; ObjectMoveService.Move now does a single HashSet lookup.",
            "https://github.com/VRCFury/VRCFury/commit/19a592b4e12d6f5f66fdc0150b60cd3206a13ed8");

        internal override void Install(Harmony harmony, VrcfuryCompat compat) { }
    }
}
