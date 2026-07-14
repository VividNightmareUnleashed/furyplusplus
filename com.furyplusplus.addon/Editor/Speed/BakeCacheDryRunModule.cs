using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Incremental bake cache — DRY-RUN TELEMETRY ONLY. On every
     * play-mode bake this computes a conservative fingerprint of everything that could
     * influence the build (avatar hierarchy + serialized components with stabilized
     * references, content hashes of every referenced asset, build config, VRCFury +
     * FuryPlusPlus versions/settings, the installed preprocessor callback list) and logs
     * whether a cache would have HIT — and how much time it would have saved — without
     * ever replaying anything.
     *
     * The fingerprint's false-hit rate can only be judged across many real sessions,
     * which is exactly what this module measures. Stage 3 (BakeCacheReplayModule) shares
     * this module's chain anchor: ChainPrefix hosts both the telemetry and the replay
     * hand-off, so one fingerprint computation serves both and a replayed call can never
     * pollute the telemetry records.
     *
     * Stage 2 (NDMF-aware anchor): the same fingerprint is additionally taken at
     * VRCBuildPipelineCallbacks.OnPreprocessAvatar — before the whole preprocessor chain
     * (NDMF at -11000, VRCFury at -10000, NDMF optimize at -1025) has touched the avatar.
     * At that point the avatar is pure user-authored state and references no per-bake
     * regenerated containers, so this fingerprint can hit on Modular Avatar avatars where
     * the RunMain anchor provably cannot (NDMF rewrites its generated containers with
     * fresh random sub-asset localIds every bake; measured 2026-07-13). Two verdict lines
     * per bake compare the anchors on the same sessions.
     */
    internal sealed class BakeCacheDryRunModule : Module {
        internal static BakeCacheDryRunModule Instance { get; private set; }

        internal BakeCacheDryRunModule() {
            Instance = this;
        }

        internal override string Id => "bakeCacheDryRun";
        internal override string DisplayName => "Bake cache dry-run telemetry (⚗️EXPERIMENTAL)";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override string Description =>
            "Measures — without changing anything — whether an incremental bake cache would " +
            "have skipped each play-mode bake, and logs the verdict plus the time it would " +
            "have saved. Fingerprints two anchors per bake: the whole preprocessor chain " +
            "(pre-NDMF, can hit on Modular Avatar avatars) and VRCFury's main build. Leave " +
            "on across normal work sessions; the log lines are the evidence for (or against) " +
            "enabling a real cache later.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            BakeCacheDryRunPatch.Install(harmony);
        }

        internal override string ReportStats() {
            var runMain = BakeCacheDryRunPatch.LastStats;
            var chain = BakeCacheDryRunPatch.LastChainStats;
            if (runMain == null) return chain;
            if (chain == null) return runMain;
            return runMain + " | " + chain;
        }
    }

    internal static class BakeCacheDryRunPatch {
        internal static string LastStats;
        internal static string LastChainStats;

        [Serializable]
        private class CacheRecord {
            public string hierarchyHash;
            public string assetsHash;
            public string configHash;
            public string generatedHash;
            public bool upstreamGenerated;
            public double lastBakeSeconds;
            public int wouldHit;
            public int wouldMiss;
        }

        private static System.Diagnostics.Stopwatch bakeTimer;
        private static string pendingKey;
        private static CacheRecord pendingRecord;

        // Chain-anchor state, deliberately separate from the RunMain fields: RunMain fires
        // INSIDE the chain, and its prefix resets its own pending state on entry.
        private static System.Diagnostics.Stopwatch chainTimer;
        private static string chainKey;
        private static CacheRecord chainRecord;
        private static int chainDepth;
        private static readonly HashSet<int> chainSeen = new HashSet<int>();

        // Stage-3 replay/capture state for the current chain call (see BakeCacheReplayModule).
        private static BakeFingerprint.Result chainFingerprint;
        private static GameObject chainAvatar;
        private static string snapshotKey;
        private static bool chainTelemetry;
        private static bool chainCapture;
        private static bool chainSnapshotValid;
        private static bool replayedThisCall;
        private static readonly HashSet<string> ineligibleLogged = new HashSet<string>();

        private static string DirPath => BakeFingerprint.DirPath;

        private static Type vrcfuryTestType;
        private static System.Reflection.MethodInfo isActuallyUploading;
        private static System.Reflection.FieldInfo vfGameObjectField;

        internal static void Install(Harmony harmony) {
            // Anchored on VRCFuryBuilder.RunMain: play-mode bakes reach it through EVERY
            // initiator (SDK callbacks, Gesture Manager, Av3Emu, NDMF apply-on-play),
            // unlike any single outer entrypoint. The fingerprint therefore measures
            // "would VRCFury's main build have been skippable" — the dominant bake cost.
            vrcfuryTestType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFuryTest"), "VF.Model.VRCFuryTest");
            isActuallyUploading = ReflectionUtils.Demand(
                ReflectionUtils.FindMethodWithSignature(
                    ReflectionUtils.FindType("VF.Hooks.IsActuallyUploadingHook"), "Get", typeof(bool)),
                "IsActuallyUploadingHook.Get()");
            var vfGameObjectType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VFGameObject"), "VF.Utils.VFGameObject");
            vfGameObjectField = ReflectionUtils.Demand(
                vfGameObjectType.GetField("_gameObject",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public),
                "VFGameObject._gameObject");
            var runMain = ReflectionUtils.Demand(Bootstrap.Compat?.RunMain, "VRCFuryBuilder.RunMain");
            harmony.Patch(runMain,
                prefix: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ProcessPrefix)),
                finalizer: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ProcessFinalizer)));

            // Stage 2 anchor: the whole preprocess chain runs inside this one SDK call, so
            // prefix/finalizer bracket exactly what a full-chain cache would skip. VRCFury
            // patches the same method (RunPreprocessorsOnlyOncePatch) with a prefix that
            // short-circuits repeat calls; Priority.First runs our fingerprint before it.
            PreprocessChainCompat.EnsureResolved();
            var onPreprocess = ReflectionUtils.Demand(
                PreprocessChainCompat.OnPreprocessAvatar,
                "VRCBuildPipelineCallbacks.OnPreprocessAvatar(GameObject)");
            harmony.Patch(onPreprocess,
                prefix: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ChainPrefix)) {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ChainFinalizer)) {
                    priority = Priority.First
                });

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) {
                chainSeen.Clear();
                ineligibleLogged.Clear();
                chainDepth = 0;
            }
            // The replay leaves its progress bar up through the silent play transition.
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode) {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void ProcessPrefix(object __0) {
            pendingKey = null;
            pendingRecord = null;
            bakeTimer = null;
            if (BakeCacheDryRunModule.Instance?.Enabled != true) return;
            if (!Application.isPlaying) return;

            try {
                bool uploading;
                try {
                    uploading = (bool)isActuallyUploading.Invoke(null, null);
                } catch {
                    uploading = true;
                }
                if (uploading) return;

                var avatarObject = __0 == null ? null : vfGameObjectField.GetValue(__0) as GameObject;
                if (avatarObject == null) return;
                // A post-bake fingerprint is garbage (random [VF###] names, regenerated
                // container fileIDs) and must never overwrite the record.
                if (avatarObject.GetComponent(vrcfuryTestType) != null) return;

                var hashTimer = System.Diagnostics.Stopwatch.StartNew();
                var record = ToRecord(BakeFingerprint.Compute(avatarObject, ""));
                hashTimer.Stop();

                // Initiators rename the processed root (e.g. "(Clone)"); normalize.
                var avatarName = avatarObject.name.Replace("(Clone)", "").Trim();
                pendingKey = BakeFingerprint.SanitizeKey(avatarObject.scene.name + "__" + avatarName);
                pendingRecord = record;

                CacheRecord previous = null;
                var file = Path.Combine(DirPath, pendingKey + ".json");
                if (File.Exists(file)) {
                    try {
                        previous = JsonUtility.FromJson<CacheRecord>(File.ReadAllText(file));
                    } catch {
                        previous = null;
                    }
                }

                // Upstream-generated containers are content-hashed like any other
                // dependency, so hits are real measurements even with NDMF/MA present —
                // but a replay stage would additionally need to validate that upstream
                // output is what the cached bake consumed, hence the annotation.
                var note = record.upstreamGenerated
                    ? " Avatar references NDMF/MA-generated assets (identified by filename, content-hashed)."
                    : "";
                if (previous == null) {
                    record.wouldMiss = 1;
                    Log.Info($"Bake cache (dry run): first bake recorded for '{avatarObject.name}' " +
                             $"(fingerprint took {hashTimer.ElapsedMilliseconds} ms).{note}");
                } else {
                    record.wouldHit = previous.wouldHit;
                    record.wouldMiss = previous.wouldMiss;
                    string missReason = null;
                    if (previous.hierarchyHash != record.hierarchyHash) missReason = "avatar hierarchy/components changed";
                    else if (previous.assetsHash != record.assetsHash) missReason = "a referenced asset changed";
                    else if (previous.configHash != record.configHash) missReason = "build/addon configuration changed";
                    else if (previous.generatedHash != record.generatedHash) missReason =
                        "an NDMF/MA-generated asset changed (an earlier build tool regenerates it every bake, " +
                        "so a cache anchored at VRCFury cannot hit on this avatar without NDMF-aware integration)";

                    if (missReason == null) {
                        record.wouldHit++;
                        Log.Info($"Bake cache (dry run): would have HIT for '{avatarObject.name}' — " +
                                 $"saving ~{previous.lastBakeSeconds:F1}s " +
                                 $"(fingerprint took {hashTimer.ElapsedMilliseconds} ms; " +
                                 $"session tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                    } else {
                        record.wouldMiss++;
                        Log.Info($"Bake cache (dry run): would MISS for '{avatarObject.name}' — {missReason} " +
                                 $"(fingerprint took {hashTimer.ElapsedMilliseconds} ms; " +
                                 $"tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                    }
                    LastStats = $"runMain: wouldHit={record.wouldHit} wouldMiss={record.wouldMiss}";
                }

                bakeTimer = System.Diagnostics.Stopwatch.StartNew();
            } catch (Exception e) {
                Log.Warn("Bake cache telemetry skipped: " + e.Message);
                pendingKey = null;
                pendingRecord = null;
            }
        }

        private static Exception ProcessFinalizer(Exception __exception) {
            if (pendingKey != null && pendingRecord != null) {
                try {
                    if (bakeTimer != null) {
                        bakeTimer.Stop();
                        pendingRecord.lastBakeSeconds = bakeTimer.Elapsed.TotalSeconds;
                    }
                    // Only record fingerprints of SUCCESSFUL bakes as cache candidates.
                    if (__exception == null) {
                        Directory.CreateDirectory(DirPath);
                        File.WriteAllText(
                            Path.Combine(DirPath, pendingKey + ".json"),
                            JsonUtility.ToJson(pendingRecord, true));
                    }
                } catch (Exception e) {
                    Log.Warn("Bake cache telemetry could not persist: " + e.Message);
                }
            }
            pendingKey = null;
            pendingRecord = null;
            bakeTimer = null;
            return __exception;
        }

        // ---- stage 2: whole-chain anchor (pre-NDMF) ----

        private static bool ChainPrefix(GameObject __0, ref bool __result) {
            // Only the outermost call is "the chain"; nothing in the chain is known to
            // recurse into OnPreprocessAvatar, but the guard costs nothing.
            if (chainDepth++ != 0) return true;
            chainKey = null;
            chainRecord = null;
            chainTimer = null;
            chainFingerprint = null;
            chainAvatar = null;
            snapshotKey = null;
            chainTelemetry = false;
            chainCapture = false;
            chainSnapshotValid = false;
            replayedThisCall = false;

            var dryRunOn = BakeCacheDryRunModule.Instance?.Enabled == true;
            var replayModule = BakeCacheReplayModule.Instance;
            var replayOn = replayModule != null && ModuleRegistry.IsActive(replayModule)
                           && replayModule.Enabled;
            if (!dryRunOn && !replayOn) return true;
            if (!Application.isPlaying) return true;

            try {
                bool uploading;
                try {
                    uploading = (bool)isActuallyUploading.Invoke(null, null);
                } catch {
                    uploading = true;
                }
                if (uploading) return true;

                var avatarObject = __0;
                if (avatarObject == null) return true;
                // Post-processed rerun (VRCFury tags processed play-mode avatars).
                if (avatarObject.GetComponent(vrcfuryTestType) != null) return true;
                // A replayed avatar carries NDMF's completed tag; unlike the static sets
                // below it survives everything short of a domain reload, and a normally
                // baked avatar with the tag is deduped by VRCFury exactly as stock.
                if (NdmfCompat.IsMarkedProcessed(avatarObject)) return true;
                // NDMF (HookDedup) and VRCFury (RunPreprocessorsOnlyOncePatch) both dedup
                // repeat chain invocations per play session; mirror that so a suppressed
                // second call can never overwrite the pre-chain fingerprint.
                if (!chainSeen.Add(avatarObject.GetInstanceID())) return true;

                var hashTimer = System.Diagnostics.Stopwatch.StartNew();
                var fingerprint = BakeFingerprint.Compute(avatarObject, "chain-");
                var record = ToRecord(fingerprint);
                hashTimer.Stop();

                var avatarName = avatarObject.name.Replace("(Clone)", "").Trim();
                var key = BakeFingerprint.SanitizeKey(avatarObject.scene.name + "__" + avatarName);
                chainKey = key + ".chain";
                chainRecord = record;
                chainFingerprint = fingerprint;
                chainAvatar = avatarObject;
                snapshotKey = key;
                chainTelemetry = dryRunOn;
                chainCapture = replayOn;

                if (dryRunOn) {
                    CacheRecord previous = null;
                    var file = Path.Combine(DirPath, chainKey + ".json");
                    if (File.Exists(file)) {
                        try {
                            previous = JsonUtility.FromJson<CacheRecord>(File.ReadAllText(file));
                        } catch {
                            previous = null;
                        }
                    }

                    // Pre-chain, the avatar must not reference per-bake generated containers.
                    // If it does, an earlier bake leaked state into the scene and a chain-level
                    // cache could never be trusted for this avatar — surface it loudly.
                    var note = record.upstreamGenerated
                        ? " WARNING: avatar references NDMF/MA-generated assets BEFORE the build chain ran (leaked state)."
                        : "";
                    if (previous == null) {
                        record.wouldMiss = 1;
                        Log.Info($"Bake cache (dry run, whole-chain anchor): first bake recorded for " +
                                 $"'{avatarObject.name}' (fingerprint took {hashTimer.ElapsedMilliseconds} ms).{note}");
                    } else {
                        record.wouldHit = previous.wouldHit;
                        record.wouldMiss = previous.wouldMiss;
                        string missReason = null;
                        if (previous.hierarchyHash != record.hierarchyHash) missReason = "avatar hierarchy/components changed";
                        else if (previous.assetsHash != record.assetsHash) missReason = "a referenced asset changed";
                        else if (previous.configHash != record.configHash) missReason = "build/addon/plugin configuration changed";
                        else if (previous.generatedHash != record.generatedHash) missReason =
                            "pre-chain generated-asset references changed (leaked state from an earlier bake)";

                        if (missReason == null) {
                            record.wouldHit++;
                            Log.Info($"Bake cache (dry run, whole-chain anchor): would have HIT for " +
                                     $"'{avatarObject.name}' — saving ~{previous.lastBakeSeconds:F1}s of the full " +
                                     $"NDMF+VRCFury chain (fingerprint took {hashTimer.ElapsedMilliseconds} ms; " +
                                     $"tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                        } else {
                            record.wouldMiss++;
                            Log.Info($"Bake cache (dry run, whole-chain anchor): would MISS for " +
                                     $"'{avatarObject.name}' — {missReason} " +
                                     $"(fingerprint took {hashTimer.ElapsedMilliseconds} ms; " +
                                     $"tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                        }
                        LastChainStats = $"chain: wouldHit={record.wouldHit} wouldMiss={record.wouldMiss}";
                    }
                }

                if (replayOn) {
                    if (!fingerprint.ReplayEligible) {
                        if (ineligibleLogged.Add(key)) {
                            var reason = fingerprint.ExternalSceneRefs.Count > 0
                                ? "it references scene objects outside the avatar ("
                                  + string.Join(", ", fingerprint.ExternalSceneRefs.GetRange(
                                      0, Math.Min(3, fingerprint.ExternalSceneRefs.Count))) + ")"
                                : "it references NDMF/MA-generated assets before the chain ran (leaked state)";
                            Log.Info($"Bake cache: '{avatarObject.name}' cannot be replay-cached — {reason}.");
                        }
                    } else if (BakeCacheSnapshotStore.TryLoad(key, fingerprint, out var snapshot)) {
                        if (Settings.IsOptionEnabled(replayModule, BakeCacheReplayModule.CaptureOnly)) {
                            chainSnapshotValid = true; // still fresh; skip the re-capture
                            Log.Info($"Bake cache: would have REPLAYED '{avatarObject.name}' " +
                                     $"(~{snapshot.Meta.chainSeconds:F1}s) — capture-only validation " +
                                     "mode, chain runs normally.");
                        } else if (BakeCacheReplayPatch.TryReplay(avatarObject, snapshot)) {
                            replayedThisCall = true;
                            __result = true;
                            return false; // skip the entire preprocessor chain
                        }
                        // TryReplay pre-destructive failure: snapshot is suspect, fall
                        // through to a normal bake and let the finalizer re-capture.
                    }
                }

                chainTimer = System.Diagnostics.Stopwatch.StartNew();
                return true;
            } catch (Exception e) {
                Log.Warn("Bake cache chain telemetry skipped: " + e.Message);
                chainKey = null;
                chainRecord = null;
                chainFingerprint = null;
                chainAvatar = null;
                snapshotKey = null;
                return true;
            }
        }

        private static Exception ChainFinalizer(Exception __exception, bool __result) {
            if (chainDepth > 0) chainDepth--;
            if (chainDepth != 0) return __exception;
            try {
                // A ~0.1s replay must never masquerade as a bake: no record, no capture.
                if (replayedThisCall) return __exception;
                if (chainKey == null || chainRecord == null) return __exception;
                if (chainTimer != null) {
                    chainTimer.Stop();
                    chainRecord.lastBakeSeconds = chainTimer.Elapsed.TotalSeconds;
                }
                // Only successful chains (no exception AND every callback returned true)
                // are cache candidates — for the record and the snapshot alike.
                if (__exception != null || !__result) return __exception;

                if (chainTelemetry) {
                    try {
                        Directory.CreateDirectory(DirPath);
                        File.WriteAllText(
                            Path.Combine(DirPath, chainKey + ".json"),
                            JsonUtility.ToJson(chainRecord, true));
                    } catch (Exception e) {
                        Log.Warn("Bake cache chain telemetry could not persist: " + e.Message);
                    }
                }

                if (chainCapture && !chainSnapshotValid && chainAvatar != null
                    && chainFingerprint != null && chainFingerprint.ReplayEligible) {
                    try {
                        BakeCacheSnapshotStore.Capture(
                            chainAvatar, snapshotKey, chainFingerprint, chainRecord.lastBakeSeconds);
                    } catch (Exception e) {
                        Log.Warn("Bake cache: snapshot capture failed (bake unaffected): " + e.Message);
                    }
                }

                // Parity diagnostic: dump the processed hierarchy after a REAL bake so it can
                // be diffed against the post-replay dump (see BakeCacheReplayPatch).
                if (chainAvatar != null
                    && EditorPrefs.GetBool(Settings.KeyPrefix + "bakeCache.parityDump", false)) {
                    try {
                        Directory.CreateDirectory(DirPath);
                        File.WriteAllText(Path.Combine(DirPath, "parity-fresh.txt"),
                            BakeFingerprint.DumpHierarchy(chainAvatar));
                    } catch {
                        // diagnostics only
                    }
                }
                return __exception;
            } finally {
                chainKey = null;
                chainRecord = null;
                chainTimer = null;
                chainFingerprint = null;
                chainAvatar = null;
                snapshotKey = null;
                chainTelemetry = false;
                chainCapture = false;
                chainSnapshotValid = false;
                replayedThisCall = false;
            }
        }

        /** Fingerprint → persisted record; hit/miss tallies and timing are filled in later. */
        private static CacheRecord ToRecord(BakeFingerprint.Result result) {
            return new CacheRecord {
                hierarchyHash = result.HierarchyHash,
                assetsHash = result.AssetsHash,
                configHash = result.ConfigHash,
                generatedHash = result.GeneratedHash,
                upstreamGenerated = result.UpstreamGenerated
            };
        }
    }
}
