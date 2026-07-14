using System;
using HarmonyLib;
using UnityEditor;

namespace FuryPlusPlus {
    /**
     * Skips VRCFury's end-of-bake disk serialization during play-mode test builds.
     * SaveAssetsService.Run still executes — its FinalizeAsset step flushes the clip
     * ext-db into the real clips, which the baked avatar needs — but the actual disk
     * writes (SaveAsset/AttachAsset/work-log manifest) are no-oped: play-mode avatars run
     * entirely off in-memory object references (Av3Emu / Gesture Manager audited).
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

        private static bool scopeActive;
        private static bool suppressPruneThisPlay;
        private static int skippedWrites;
        private static bool subscribed;

        internal static void Install(Harmony harmony) {
            var assetDbType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VRCFuryAssetDatabase"), "VF.Utils.VRCFuryAssetDatabase");
            var saveAsset2 = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(assetDbType, "SaveAsset",
                    method => method.GetParameters().Length == 2),
                "VRCFuryAssetDatabase.SaveAsset(obj, fullPath)");
            var saveAsset3 = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(assetDbType, "SaveAsset",
                    method => method.GetParameters().Length == 3),
                "VRCFuryAssetDatabase.SaveAsset(obj, dir, filename)");
            var attachAsset = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(assetDbType, "AttachAsset",
                    method => method.GetParameters().Length == 2),
                "VRCFuryAssetDatabase.AttachAsset(obj, parent)");
            var sessionType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.SaveAssetsSession"), "VF.Utils.SaveAssetsSession");
            var flushManifest = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(sessionType, "FlushWorkLogManifest",
                    method => method.GetParameters().Length == 1),
                "SaveAssetsSession.FlushWorkLogManifest(dir)");
            var saveServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.SaveAssetsService"), "VF.Service.SaveAssetsService");
            var saveRun = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(saveServiceType, "Run",
                    method => method.GetParameters().Length == 0),
                "SaveAssetsService.Run()");
            var factoryType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory"), "VF.Utils.VrcfObjectFactory");
            var prune = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(factoryType, "Prune",
                    method => method.GetParameters().Length == 0),
                "VrcfObjectFactory.Prune()");
            UploadCompat.DemandCore();

            harmony.Patch(saveRun,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(RunFinalizer)));
            harmony.Patch(saveAsset2,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(saveAsset3,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(attachAsset,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(flushManifest,
                prefix: new HarmonyMethod(typeof(PlayModeNoDiskSavePatch), nameof(SkipInScopePrefix)));
            harmony.Patch(prune,
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
            }
        }

        private static Exception RunFinalizer(Exception __exception) {
            if (scopeActive) {
                scopeActive = false;
                if (skippedWrites > 0) {
                    Log.Info($"Play-mode bake kept in memory: skipped {skippedWrites} disk write(s) " +
                             "(assets are not persisted; exit/re-enter play after script changes).");
                    LastStats = $"skippedWrites={skippedWrites}";
                    LastSkippedWrites = skippedWrites;
                }
            }
            return __exception;
        }

        private static bool SkipInScopePrefix() {
            if (!scopeActive) return true;
            skippedWrites++;
            return false;
        }

        private static bool PrunePrefix() {
            return !(suppressPruneThisPlay && UnityEngine.Application.isPlaying);
        }
    }
}
