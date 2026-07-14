using System;
using System.IO;
using HarmonyLib;
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
     * which is exactly what this module measures. Two anchors per bake, evaluated by ONE
     * shared ledger (EvaluateTelemetry) so the verdict lines stay comparable:
     *  - VRCFuryBuilder.RunMain (this module's own patch): "would VRCFury's main build
     *    have been skippable" — the dominant bake cost;
     *  - the whole preprocessor chain, via the framework BakeChainAnchor — before NDMF
     *    (-11000), VRCFury (-10000) and NDMF optimize (-1025) touched the avatar. At that
     *    point the avatar is pure user-authored state, so this fingerprint can hit on
     *    Modular Avatar avatars where the RunMain anchor provably cannot (NDMF rewrites
     *    its generated containers with fresh random sub-asset localIds every bake;
     *    measured 2026-07-13).
     */
    internal sealed class BakeCacheDryRunModule : Module<BakeCacheDryRunModule> {
        internal override string Id => "bakeCacheDryRun";
        internal override string DisplayName => "Bake cache dry-run telemetry (⚗️EXPERIMENTAL)";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override string SettingsGroup => "Play-mode iteration";
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

        internal override (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) {
            var (hits, misses) = BakeCacheDryRunPatch.LastTally;
            return hits + misses > 0
                ? ($"would hit {hits}/{hits + misses} bakes",
                    "How often an incremental bake cache could have skipped the bake.")
                : ((string, string)?)null;
        }
    }

    internal static class BakeCacheDryRunPatch {
        internal static string LastStats;
        internal static string LastChainStats;

        // Typed last-bake tallies behind the stats strings (RunMain anchor preferred).
        private static int runMainHits = -1;
        private static int runMainMisses = -1;
        private static int chainHits = -1;
        private static int chainMisses = -1;

        internal static (int Hits, int Misses) LastTally =>
            runMainHits >= 0 ? (runMainHits, runMainMisses)
            : chainHits >= 0 ? (chainHits, chainMisses)
            : (0, 0);

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

        private static string DirPath => BakeFingerprint.DirPath;

        private static Type vrcfuryTestType;

        internal static void Install(Harmony harmony) {
            // Anchored on VRCFuryBuilder.RunMain: play-mode bakes reach it through EVERY
            // initiator (SDK callbacks, Gesture Manager, Av3Emu, NDMF apply-on-play),
            // unlike any single outer entrypoint. The fingerprint therefore measures
            // "would VRCFury's main build have been skippable" — the dominant bake cost.
            vrcfuryTestType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFuryTest"), "VF.Model.VRCFuryTest");
            UploadCompat.DemandCore();
            VfGameObjectCompat.DemandCore();
            var runMain = ReflectionUtils.Demand(Bootstrap.Compat?.RunMain, "VRCFuryBuilder.RunMain");
            harmony.Patch(runMain,
                prefix: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ProcessPrefix)),
                finalizer: new HarmonyMethod(typeof(BakeCacheDryRunPatch), nameof(ProcessFinalizer)));

            // Stage 2 anchor: the whole-chain fingerprint, computed once by the shared
            // framework anchor and handed to this telemetry participant.
            BakeChainAnchor.EnsureInstalled(harmony);
            BakeChainAnchor.Register(ChainTelemetry.Singleton);
        }

        private static void ProcessPrefix(object __0) {
            pendingKey = null;
            pendingRecord = null;
            bakeTimer = null;
            if (BakeCacheDryRunModule.Instance?.Enabled != true) return;
            if (!Application.isPlaying) return;

            try {
                // Never touch a maybe-upload: a resolution failure assumes uploading.
                if (UploadCompat.IsActuallyUploading(assumeOnFailure: true)) return;

                var avatarObject = VfGameObjectCompat.Unwrap(__0);
                if (avatarObject == null) return;
                // A post-bake fingerprint is garbage (random [VF###] names, regenerated
                // container fileIDs) and must never overwrite the record.
                if (avatarObject.GetComponent(vrcfuryTestType) != null) return;

                var hashTimer = System.Diagnostics.Stopwatch.StartNew();
                var fingerprint = BakeFingerprint.Compute(avatarObject, "");
                hashTimer.Stop();

                // Initiators rename the processed root (e.g. "(Clone)"); normalize.
                pendingKey = BakeFingerprint.SanitizeKey(avatarObject.scene.name + "__"
                    + BakeFingerprint.NormalizeAvatarName(avatarObject.name));
                pendingRecord = EvaluateTelemetry(
                    pendingKey, fingerprint, avatarObject.name,
                    hashTimer.ElapsedMilliseconds, chainAnchor: false);

                bakeTimer = System.Diagnostics.Stopwatch.StartNew();
            } catch (Exception e) {
                Log.Warn("Bake cache telemetry skipped: " + e.Message);
                pendingKey = null;
                pendingRecord = null;
            }
        }

        private static Exception ProcessFinalizer(Exception __exception) {
            if (pendingKey != null && pendingRecord != null) {
                if (bakeTimer != null) {
                    bakeTimer.Stop();
                    pendingRecord.lastBakeSeconds = bakeTimer.Elapsed.TotalSeconds;
                }
                // Only record fingerprints of SUCCESSFUL bakes as cache candidates.
                if (__exception == null) PersistRecord(pendingKey, pendingRecord);
            }
            pendingKey = null;
            pendingRecord = null;
            bakeTimer = null;
            return __exception;
        }

        /**
         * The one hit/miss ledger both anchors share: load the previous record, compare
         * the four hashes in precedence order, bump the tallies, log the verdict. Only
         * wording that genuinely differs between anchors branches on chainAnchor, so the
         * two verdict lines stay comparable — the whole point of running both.
         */
        private static CacheRecord EvaluateTelemetry(
            string fileKey, BakeFingerprint.Result fingerprint, string avatarDisplayName,
            long fingerprintMs, bool chainAnchor
        ) {
            var record = new CacheRecord {
                hierarchyHash = fingerprint.HierarchyHash,
                assetsHash = fingerprint.AssetsHash,
                configHash = fingerprint.ConfigHash,
                generatedHash = fingerprint.GeneratedHash,
                upstreamGenerated = fingerprint.UpstreamGenerated
            };

            CacheRecord previous = null;
            var file = Path.Combine(DirPath, fileKey + ".json");
            if (File.Exists(file)) {
                try {
                    previous = JsonUtility.FromJson<CacheRecord>(File.ReadAllText(file));
                } catch {
                    previous = null;
                }
            }

            var label = chainAnchor ? "dry run, whole-chain anchor" : "dry run";
            // Pre-chain, the avatar must not reference per-bake generated containers — if
            // it does, an earlier bake leaked state into the scene. At RunMain the same
            // references are expected (content-hashed like any other dependency).
            var note = !fingerprint.UpstreamGenerated
                ? ""
                : chainAnchor
                    ? " WARNING: avatar references NDMF/MA-generated assets BEFORE the build chain ran (leaked state)."
                    : " Avatar references NDMF/MA-generated assets (identified by filename, content-hashed).";

            if (previous == null) {
                record.wouldMiss = 1;
                Log.Info($"Bake cache ({label}): first bake recorded for '{avatarDisplayName}' " +
                         $"(fingerprint took {fingerprintMs} ms).{note}");
            } else {
                record.wouldHit = previous.wouldHit;
                record.wouldMiss = previous.wouldMiss;
                string missReason = null;
                if (previous.hierarchyHash != record.hierarchyHash) {
                    missReason = "avatar hierarchy/components changed";
                } else if (previous.assetsHash != record.assetsHash) {
                    missReason = "a referenced asset changed";
                } else if (previous.configHash != record.configHash) {
                    missReason = chainAnchor
                        ? "build/addon/plugin configuration changed"
                        : "build/addon configuration changed";
                } else if (previous.generatedHash != record.generatedHash) {
                    missReason = chainAnchor
                        ? "pre-chain generated-asset references changed (leaked state from an earlier bake)"
                        : "an NDMF/MA-generated asset changed (an earlier build tool regenerates it every bake, " +
                          "so a cache anchored at VRCFury cannot hit on this avatar without NDMF-aware integration)";
                }

                if (missReason == null) {
                    record.wouldHit++;
                    var what = chainAnchor
                        ? $"saving ~{previous.lastBakeSeconds:F1}s of the full NDMF+VRCFury chain"
                        : $"saving ~{previous.lastBakeSeconds:F1}s";
                    Log.Info($"Bake cache ({label}): would have HIT for '{avatarDisplayName}' — {what} " +
                             $"(fingerprint took {fingerprintMs} ms; " +
                             $"tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                } else {
                    record.wouldMiss++;
                    Log.Info($"Bake cache ({label}): would MISS for '{avatarDisplayName}' — {missReason} " +
                             $"(fingerprint took {fingerprintMs} ms; " +
                             $"tally {record.wouldHit} hit / {record.wouldMiss} miss).{note}");
                }

                if (chainAnchor) {
                    chainHits = record.wouldHit;
                    chainMisses = record.wouldMiss;
                    LastChainStats = $"chain: wouldHit={record.wouldHit} wouldMiss={record.wouldMiss}";
                } else {
                    runMainHits = record.wouldHit;
                    runMainMisses = record.wouldMiss;
                    LastStats = $"runMain: wouldHit={record.wouldHit} wouldMiss={record.wouldMiss}";
                }
            }
            return record;
        }

        private static void PersistRecord(string fileKey, CacheRecord record) {
            try {
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(
                    Path.Combine(DirPath, fileKey + ".json"),
                    JsonUtility.ToJson(record, true));
            } catch (Exception e) {
                Log.Warn("Bake cache telemetry could not persist: " + e.Message);
            }
        }

        /** The whole-chain anchor's telemetry, fed by the shared framework anchor. */
        private sealed class ChainTelemetry : BakeChainAnchor.Participant {
            internal static readonly ChainTelemetry Singleton = new ChainTelemetry();

            private CacheRecord record;

            internal override bool Enabled => BakeCacheDryRunModule.Instance?.Enabled == true;

            internal override bool OnChainStart(BakeChainAnchor.ChainContext context) {
                record = null; // a failed previous chain must never persist under this key
                record = EvaluateTelemetry(
                    context.Key + ".chain", context.Fingerprint, context.Avatar.name,
                    context.FingerprintMs, chainAnchor: true);
                return false;
            }

            internal override void OnChainSuccess(BakeChainAnchor.ChainContext context) {
                if (record == null) return;
                record.lastBakeSeconds = context.ChainSeconds;
                PersistRecord(context.Key + ".chain", record);
                record = null;
            }
        }
    }
}
