using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Skips upload-only VRCFury passes during play-mode test builds. Each skipped service
     * exists solely to satisfy an SDK upload rule or to warn at upload time:
     *  - FixMipmapStreamingService clones textures + materials (and rewrites material
     *    references in clips) purely for the SDK's mipmap-streaming validation.
     *  - FixMenuIconTexturesService resizes/compresses menu icons to SDK limits; the
     *    Gesture Manager renders original icons fine.
     *  - FinalValidationService only emits warnings when not actually uploading.
     * Services whose behavior users legitimately test in play mode (bounding-box fix,
     * Quest material stripping, audio/contact fixes) are deliberately NOT skipped.
     */
    internal sealed class PlayModeSkipsModule : Module<PlayModeSkipsModule> {
        // The skips change the play-mode processed avatar, so they are bake-output
        // affecting for the bake-cache config key even though uploads are untouched.
        internal static readonly ModuleOption SkipMipmapStreaming = new ModuleOption(
            "skipMipmapStreaming", "Skip mipmap-streaming fix (upload-only SDK rule)", true,
            "Skips the texture/material cloning pass that only exists to satisfy upload validation.",
            affectsBakeOutput: true);
        internal static readonly ModuleOption SkipMenuIconFixes = new ModuleOption(
            "skipMenuIconFixes", "Skip menu icon resize/compress", true,
            "Menu icons render fine untouched in play mode.",
            affectsBakeOutput: true);
        internal static readonly ModuleOption SkipFinalValidation = new ModuleOption(
            "skipFinalValidation", "Skip final validation warnings", true,
            "Param/contact-count warnings still fire on real uploads.",
            affectsBakeOutput: true);

        private static readonly ModuleOption[] AllOptions = {
            SkipMipmapStreaming, SkipMenuIconFixes, SkipFinalValidation
        };

        internal override string Id => "playModeSkips";
        internal override string DisplayName => "Play-mode upload-only pass skipping";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Play-mode iteration";
        internal override string Description =>
            "Skips VRCFury passes that only matter for real uploads when building for play mode. " +
            "Uploads are never affected.";

        internal override IReadOnlyList<ModuleOption> Options => AllOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            PlayModeSkipsPatch.Install(harmony, compat);
        }
    }

    internal static class PlayModeSkipsPatch {
        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            UploadCompat.DemandCore();
            var mipmapApply = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.FixMipmapStreamingService"), "Apply");
            var menuIconApply = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.FixMenuIconTexturesService"), "Apply");
            var validationApply = ReflectionUtils.FindNoArgVoid(
                ReflectionUtils.FindType("VF.Service.FinalValidationService"), "Apply");

            if (mipmapApply == null || menuIconApply == null || validationApply == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                mipmapApply,
                prefix: new HarmonyMethod(typeof(PlayModeSkipsPatch), nameof(SkipMipmap))
            );
            harmony.Patch(
                menuIconApply,
                prefix: new HarmonyMethod(typeof(PlayModeSkipsPatch), nameof(SkipMenuIcons))
            );
            harmony.Patch(
                validationApply,
                prefix: new HarmonyMethod(typeof(PlayModeSkipsPatch), nameof(SkipValidation))
            );
        }

        private static bool SkipMipmap() {
            return !ShouldSkip(PlayModeSkipsModule.SkipMipmapStreaming);
        }

        private static bool SkipMenuIcons() {
            return !ShouldSkip(PlayModeSkipsModule.SkipMenuIconFixes);
        }

        private static bool SkipValidation() {
            return !ShouldSkip(PlayModeSkipsModule.SkipFinalValidation);
        }

        // Each patched Apply runs once per bake, so these are phase-boundary pref reads.
        private static bool ShouldSkip(ModuleOption option) {
            try {
                var module = PlayModeSkipsModule.Instance;
                if (module == null || !module.Enabled) return false;
                if (!Settings.IsOptionEnabled(module, option)) return false;
                if (!Application.isPlaying) return false;
                // Belt-and-braces against future SDK play-upload hybrids: never skip a
                // maybe-upload, so a resolution failure assumes uploading.
                return !UploadCompat.IsActuallyUploading(assumeOnFailure: true);
            } catch (Exception e) {
                Log.Warn("Play-mode skip fell back to VRCFury: " + e.Message);
                return false;
            }
        }
    }
}
