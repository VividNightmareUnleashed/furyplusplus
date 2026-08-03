using System;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * Skips VRCFury's end-of-bake disk serialization during play-mode test builds.
     * Each SaveAssetsService.Run(controllers) transaction still executes so controllers
     * and avatar references are finalized in memory, but SaveAsset/AttachAsset and the new
     * SaveAssetsSession.Finish disk-commit boundary are no-oped: play-mode avatars run
     * entirely off in-memory object references (Av3Emu / Gesture Manager audited). Scoping
     * the controller overload also covers the parameter compressor's late Action re-save.
     *
     * Because the baked objects then stay unsaved, VRCFury's per-tick prune (which
     * destroys factory-created objects without an asset path) would tear the avatar
     * apart one tick later — so the prune is suppressed for the remainder of the play
     * session and resumes naturally on return to edit mode.
     *
     * Experimental, default OFF. Known casualty: a domain reload while playing (script
     * recompile) loses the in-memory bake, where stock would have survived via disk.
     * Never active for uploads (IsActuallyUploadingHook gate).
     */
    internal sealed class PlayModeNoDiskSaveModule : Module<PlayModeNoDiskSaveModule> {

        internal override string Id => "playModeNoDiskSave";
        internal override string DisplayName => "Play mode: skip disk serialization (⚗️EXPERIMENTAL)";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Play-mode iteration";
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override string Description =>
            "Skips writing baked assets to disk for play-mode test builds (uploads are never " +
            "affected), removing the serialization tail from play iteration. Experimental: a " +
            "script recompile while playing loses the in-memory bake (exit and re-enter play).";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            PlayModeNoDiskSavePatch.Install(harmony);
        }

        internal override string ReportStats() {
            return PlayModeNoDiskSavePatch.LastStats;
        }

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            return PlayModeNoDiskSavePatch.LastSkippedWrites > 0
                ? ($"{N(PlayModeNoDiskSavePatch.LastSkippedWrites)} disk writes skipped last bake",
                    PlayModeNoDiskSavePatch.LastStats)
                : ((string, string)?)null;
        }
    }

    internal static class PlayModeNoDiskSavePatch {
        internal static string LastStats;
        internal static int LastSkippedWrites;
        internal static int LastSkippedFinishes;

        private static bool scopeActive;
        private static bool suppressPruneThisPlay;
        private static int skippedWrites;
        private static int skippedFinishes;
        private static bool subscribed;

        internal static void Install(Harmony harmony) {
            SaveAssetsCompat.DemandNoDiskSave();
            UploadCompat.DemandCore();

            harmony.Patch(SaveAssetsCompat.SaveAssetsRunTransaction,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(RunFinalizer)));
            harmony.Patch(SaveAssetsCompat.SaveAsset2,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(SaveAssetsCompat.SaveAsset3,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(SaveAssetsCompat.AttachAsset,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(SaveAssetsCompat.Finish,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipFinishInScopePrefix)));
            harmony.Patch(SaveAssetsCompat.FactoryPrune,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(PrunePrefix)));

            if (!subscribed) {
                subscribed = true;
                EditorApplication.playModeStateChanged += state => {
                    if (state == PlayModeStateChange.EnteredEditMode) suppressPruneThisPlay = false;
                };
            }
        }

        private static void RunPrefix() {
            var enabled = PlayModeNoDiskSaveModule.Instance?.Enabled == true;
            // Failure default: unknown → assume upload → never skip.
            scopeActive = enabled && UnityEngine.Application.isPlaying
                          && !UploadCompat.IsActuallyUploading(assumeOnFailure: true);
            if (scopeActive) {
                suppressPruneThisPlay = true;
                skippedWrites = 0;
                skippedFinishes = 0;
            }
        }

        private static Exception RunFinalizer(Exception __exception) {
            if (scopeActive) {
                scopeActive = false;
                if (skippedWrites > 0) {
                    Log.Info($"Play-mode bake kept in memory: skipped {skippedWrites} disk write(s) " +
                             "(assets are not persisted; exit/re-enter play after script changes).");
                    LastStats = $"skippedWrites={skippedWrites} finishTransactions={skippedFinishes}";
                    LastSkippedWrites = skippedWrites;
                    LastSkippedFinishes = skippedFinishes;
                }
            }
            return __exception;
        }

        private static bool SkipInScopePrefix() {
            if (!scopeActive) return true;
            skippedWrites++;
            return false;
        }

        private static bool SkipFinishInScopePrefix() {
            if (!scopeActive) return true;
            skippedWrites++;
            skippedFinishes++;
            return false;
        }

        private static bool PrunePrefix() {
            return !(suppressPruneThisPlay && UnityEngine.Application.isPlaying);
        }
    }
}
