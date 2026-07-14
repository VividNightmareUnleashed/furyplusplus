using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus {
    /**
     * Makes VRCFury's layer-to-blendtree optimizer treat ALL FX layers as eligible — not
     * just VRCFury-managed ones. LayerToTreeService widens scope exactly when a
     * DirectTreeOptimizer feature is present (that flag is read nowhere else), so this pass
     * injects a real VRCFury{content = DirectTreeOptimizer} component onto the BUILD CLONE
     * at callbackOrder −15000 (before VRCFury's main build at −10000). VRCFury discovers it
     * through its own supported path and runs its own ~11 conversion guards per user layer;
     * the clone is destroyed after the build, so nothing persists on the source avatar.
     */
    internal sealed class FullScopeDbtModule : Module<FullScopeDbtModule> {

        internal override string Id => "fullScopeDbt";
        internal override string DisplayName => "Optimize user FX layers into blendtrees";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override string SettingsGroup => "Animator layers";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Extends VRCFury's layer-to-blendtree pass to hand-authored FX layers (same effect " +
            "as adding its Direct Tree Optimizer component). Layers failing any of VRCFury's " +
            "safety checks stay untouched.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            FullScopeDbtPass.Resolve();
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return analysis?.FxLayers > 1
                ? ($"{analysis.Value.FxLayers} FX layers → blendtree",
                    "Eligible toggle layers merge into a single direct-blendtree layer; " +
                    "the exact count depends on per-layer eligibility.")
                : ((string, string)?)null;
        }
    }

    internal class FullScopeDbtPass : GuardedPreprocessorPass {
        private static Type vrcfuryComponentType;
        private static FieldInfo contentField;
        private static Type directTreeOptimizerType;

        internal static void Resolve() {
            vrcfuryComponentType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFury"), "VF.Model.VRCFury");
            contentField = ReflectionUtils.Demand(
                vrcfuryComponentType.GetField("content",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                "VRCFury.content");
            directTreeOptimizerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.Feature.DirectTreeOptimizer"),
                "VF.Model.Feature.DirectTreeOptimizer");
        }

        public override int callbackOrder => -15000;

        protected override Module GatingModule => FullScopeDbtModule.Instance;

        protected override bool Run(GameObject avatarObject) {
            if (vrcfuryComponentType == null) return true;
            if (avatarObject.GetComponent<VRCAvatarDescriptor>() == null) return true;

            // Never turn a non-VRCFury avatar into a VRCFury build — only widen scope where
            // VRCFury is already going to run.
            var components = avatarObject.GetComponentsInChildren(vrcfuryComponentType, true);
            if (components.Length == 0) return true;

            foreach (var component in components) {
                var content = contentField.GetValue(component);
                if (content != null && directTreeOptimizerType.IsInstanceOfType(content)) {
                    return true; // the user already opted in themselves
                }
            }

            var added = avatarObject.AddComponent(vrcfuryComponentType);
            contentField.SetValue(added, Activator.CreateInstance(directTreeOptimizerType));
            Log.Info("Full-scope DBT: injected DirectTreeOptimizer — user FX layers are now " +
                     "eligible for blendtree conversion this build.");
            return true;
        }
    }
}
