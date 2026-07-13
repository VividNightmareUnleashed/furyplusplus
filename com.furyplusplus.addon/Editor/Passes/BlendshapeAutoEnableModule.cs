using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FuryPlusPlus {
    /**
     * Auto-enables VRCFury's Blendshape Optimizer (bake non-animated blendshapes into the
     * mesh — VRAM and skinning savings) by injecting a real VRCFury{content=
     * BlendshapeOptimizer} component onto the build clone, same pattern as the full-scope
     * DBT pass. Guards:
     *  - never when one already exists ([FeatureOnlyOneAllowed] would fail the build);
     *  - never on avatars without VRCFury components;
     *  - never when Avatar Optimizer's Trace&Optimize is present (step aside, don't
     *    double-process);
     *  - upload-only by default (mesh clone + bake costs play-mode iteration for nothing).
     * MMD protection (default on): when the avatar lacks an MmdCompatibility feature, a
     * synthetic one is appended to allFeaturesInRun ONLY for the duration of the
     * BlendshapeOptimizer action (registered via build phase hooks), so Body MMD shapes
     * survive without activating MmdCompatibility's other effects (WD forcing, MMD layers).
     */
    internal sealed class BlendshapeAutoEnableModule : Module {
        internal static BlendshapeAutoEnableModule Instance { get; private set; }

        internal static readonly ModuleOption ProtectMmdShapes = new ModuleOption(
            "protectMmdShapes", "Protect MMD blendshapes", true,
            "Keeps MMD-named shapes on Body even without an MmdCompatibility feature — " +
            "deleting them breaks dance worlds.");
        internal static readonly ModuleOption UploadOnly = new ModuleOption(
            "uploadOnly", "Only on real uploads", true,
            "Skip during play-mode test builds to keep iteration fast.");

        internal BlendshapeAutoEnableModule() {
            Instance = this;
        }

        internal override string Id => "blendshapeAutoEnable";
        internal override string DisplayName => "Auto-enable Blendshape Optimizer";
        internal override ModuleKind Kind => ModuleKind.Quality;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override string Description =>
            "Runs VRCFury's Blendshape Optimizer on avatars that don't have the component — " +
            "non-animated blendshapes get baked into the mesh (VRAM savings).";

        internal override IReadOnlyList<ModuleOption> Options =>
            new[] { ProtectMmdShapes, UploadOnly };

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            BlendshapeAutoEnablePass.Resolve();
            BuildPhaseHooks.RegisterBefore("BlendshapeOptimizer", Id,
                _ => BlendshapeAutoEnablePass.BeginMmdWrap());
            BuildPhaseHooks.RegisterAfter("BlendshapeOptimizer", Id,
                _ => BlendshapeAutoEnablePass.EndMmdWrap());
        }
    }

    internal class BlendshapeAutoEnablePass : GuardedPreprocessorPass {
        private static Type vrcfuryComponentType;
        private static FieldInfo contentField;
        private static Type blendshapeOptimizerType;
        private static Type mmdCompatibilityType;
        private static FieldInfo allFeaturesField;
        private static MethodInfo isActuallyUploading;

        [ThreadStatic] private static bool pendingMmdWrap;
        [ThreadStatic] private static object syntheticMmd;

        internal static void Resolve() {
            vrcfuryComponentType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFury"), "VF.Model.VRCFury");
            contentField = ReflectionUtils.Demand(
                vrcfuryComponentType.GetField("content",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                "VRCFury.content");
            blendshapeOptimizerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.Feature.BlendshapeOptimizer"),
                "VF.Model.Feature.BlendshapeOptimizer");
            mmdCompatibilityType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.Feature.MmdCompatibility"),
                "VF.Model.Feature.MmdCompatibility");
            allFeaturesField = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.GlobalsService")?.GetField("allFeaturesInRun",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                "GlobalsService.allFeaturesInRun");
            isActuallyUploading = ReflectionUtils.Demand(
                ReflectionUtils.FindMethodWithSignature(
                    ReflectionUtils.FindType("VF.Hooks.IsActuallyUploadingHook"), "Get", typeof(bool)),
                "IsActuallyUploadingHook.Get()");
        }

        public override int callbackOrder => -15000;

        protected override Module GatingModule => BlendshapeAutoEnableModule.Instance;

        protected override bool Run(GameObject avatarObject) {
            pendingMmdWrap = false;
            if (vrcfuryComponentType == null) return true;
            if (avatarObject.GetComponent<VRCAvatarDescriptor>() == null) return true;

            var module = BlendshapeAutoEnableModule.Instance;
            if (Settings.IsOptionEnabled(module, BlendshapeAutoEnableModule.UploadOnly)
                && Application.isPlaying) {
                bool uploading;
                try {
                    uploading = (bool)isActuallyUploading.Invoke(null, null);
                } catch {
                    uploading = false;
                }
                if (!uploading) return true;
            }

            // Step aside for Avatar Optimizer's Trace & Optimize.
            var aaoType = ReflectionUtils.FindType("Anatawa12.AvatarOptimizer.TraceAndOptimize");
            if (aaoType != null && avatarObject.GetComponentInChildren(aaoType, true) != null) {
                return true;
            }

            var components = avatarObject.GetComponentsInChildren(vrcfuryComponentType, true);
            if (components.Length == 0) return true;

            var hasMmdCompat = false;
            foreach (var component in components) {
                var content = contentField.GetValue(component);
                if (content == null) continue;
                if (blendshapeOptimizerType.IsInstanceOfType(content)) {
                    return true; // already present — injecting again would FAIL the build
                }
                if (mmdCompatibilityType.IsInstanceOfType(content)) hasMmdCompat = true;
            }

            var added = avatarObject.AddComponent(vrcfuryComponentType);
            contentField.SetValue(added, Activator.CreateInstance(blendshapeOptimizerType));
            pendingMmdWrap = !hasMmdCompat
                             && Settings.IsOptionEnabled(module, BlendshapeAutoEnableModule.ProtectMmdShapes);
            Log.Info("Auto-enabled Blendshape Optimizer for this build" +
                     (pendingMmdWrap ? " (MMD shapes protected)." : "."));
            return true;
        }

        /** Appends a synthetic MmdCompatibility just before the optimizer action runs. */
        internal static void BeginMmdWrap() {
            if (!pendingMmdWrap) return;
            var globals = BuildPhaseHooks.GetService("VF.Service.GlobalsService");
            if (globals == null) return;
            if (!(allFeaturesField.GetValue(globals) is IList features)) return;
            syntheticMmd = Activator.CreateInstance(mmdCompatibilityType);
            features.Add(syntheticMmd);
        }

        /** Removes it before any later phase (WD forcing, MMD layers) could observe it. */
        internal static void EndMmdWrap() {
            pendingMmdWrap = false;
            if (syntheticMmd == null) return;
            var globals = BuildPhaseHooks.GetService("VF.Service.GlobalsService");
            if (globals != null && allFeaturesField.GetValue(globals) is IList features) {
                features.Remove(syntheticMmd);
            }
            syntheticMmd = null;
        }
    }
}
