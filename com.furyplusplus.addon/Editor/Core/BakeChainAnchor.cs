using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Framework-owned anchor on VRCBuildPipelineCallbacks.OnPreprocessAvatar — the single
     * entry every play-mode bake initiator funnels through (SDK pipeline, NDMF apply-on-
     * play, VRCFury's PlayModeTrigger, Av3Emu). The whole NDMF+VRCFury preprocessor chain
     * runs inside this one call, so prefix/finalizer bracket exactly what a full-chain
     * cache can skip.
     *
     * One pre-chain fingerprint is computed per outermost eligible call and dispatched to
     * registered participants (dry-run telemetry, snapshot capture/replay) in registration
     * order, so the participants stay independent modules: any subset can be enabled and
     * none owns another's control flow. A participant returning true from OnChainStart
     * REPLAYS the avatar — the anchor then skips the entire chain and suppresses every
     * success callback (a ~0.1s replay must never masquerade as a bake).
     *
     * Common eligibility is decided here once: play mode only, never uploads, never a
     * VRCFury-processed or NDMF-marked avatar, one fingerprint per avatar per play session
     * (mirrors NDMF HookDedup / VRCFury RunPreprocessorsOnlyOncePatch). VRCFury patches
     * the same method with a short-circuiting prefix; Priority.First runs ours before it.
     */
    internal static class BakeChainAnchor {
        internal sealed class ChainContext {
            internal GameObject Avatar;
            internal BakeFingerprint.Result Fingerprint;
            /** Sanitized "scene__avatar" — the snapshot key; telemetry appends ".chain". */
            internal string Key;
            internal long FingerprintMs;
            /** Filled just before OnChainSuccess fires. */
            internal double ChainSeconds;
        }

        internal abstract class Participant {
            /** Latched once per chain at the prefix (EditorPrefs at phase boundaries only). */
            internal abstract bool Enabled { get; }

            /** Return true to replay: the anchor skips the whole preprocessor chain. */
            internal virtual bool OnChainStart(ChainContext context) => false;

            /** Chain completed (no exception, every callback true, no replay). */
            internal virtual void OnChainSuccess(ChainContext context) { }

            /** Play-mode transitions, for per-session log/UI state. */
            internal virtual void OnPlayModeChanged(PlayModeStateChange state) { }
        }

        private sealed class ChainState {
            internal ChainContext Context;
            internal List<Participant> Active;
            internal System.Diagnostics.Stopwatch Timer;
        }

        private static readonly List<Participant> Participants = new List<Participant>();
        private static bool installed;
        private static Type vrcfuryTestType;

        private static int chainDepth;
        private static readonly HashSet<int> ChainSeen = new HashSet<int>();
        // Non-null only inside an outermost eligible call; one object instead of parallel
        // per-call statics so a missed reset can never leak state into the next bake.
        private static ChainState state;

        /** Idempotent; every consuming module calls this from Install() (fail-closed). */
        internal static void EnsureInstalled(Harmony harmony) {
            if (installed) return;

            vrcfuryTestType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.VRCFuryTest"), "VF.Model.VRCFuryTest");
            UploadCompat.DemandCore();
            NdmfCompat.EnsureResolved(); // fail-soft: absence is fine, resolution is not risky
            PreprocessChainCompat.EnsureResolved();
            var onPreprocess = ReflectionUtils.Demand(
                PreprocessChainCompat.OnPreprocessAvatar,
                "VRCBuildPipelineCallbacks.OnPreprocessAvatar(GameObject)");

            harmony.Patch(onPreprocess,
                prefix: new HarmonyMethod(typeof(BakeChainAnchor), nameof(ChainPrefix)) {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(typeof(BakeChainAnchor), nameof(ChainFinalizer)) {
                    priority = Priority.First
                });

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            installed = true;
        }

        /** Bootstrap clears patches per domain load; participants re-register in install order. */
        internal static void Register(Participant participant) {
            if (!installed) {
                throw new InvalidOperationException("BakeChainAnchor is not installed");
            }
            if (!Participants.Contains(participant)) Participants.Add(participant);
        }

        /** Bootstrap removed every patch (mid-session re-init); require a fresh install. */
        internal static void NotifyUnpatched() {
            installed = false;
            Participants.Clear();
            state = null;
            chainDepth = 0;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange stateChange) {
            if (stateChange == PlayModeStateChange.ExitingPlayMode
                || stateChange == PlayModeStateChange.EnteredEditMode) {
                ChainSeen.Clear();
                chainDepth = 0;
                state = null;
            }
            foreach (var participant in Participants) {
                try {
                    participant.OnPlayModeChanged(stateChange);
                } catch {
                    // session bookkeeping only
                }
            }
        }

        private static bool ChainPrefix(GameObject __0, ref bool __result) {
            // Only the outermost call is "the chain"; nothing in the chain is known to
            // recurse into OnPreprocessAvatar, but the guard costs nothing.
            if (chainDepth++ != 0) return true;
            state = null;

            List<Participant> active = null;
            foreach (var participant in Participants) {
                if (!participant.Enabled) continue;
                (active = active ?? new List<Participant>()).Add(participant);
            }
            if (active == null) return true;
            if (!Application.isPlaying) return true;

            try {
                // Never touch a maybe-upload: a resolution failure assumes uploading.
                if (UploadCompat.IsActuallyUploading(assumeOnFailure: true)) return true;

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
                if (!ChainSeen.Add(avatarObject.GetInstanceID())) return true;

                var hashTimer = System.Diagnostics.Stopwatch.StartNew();
                var fingerprint = BakeFingerprint.Compute(avatarObject, "chain-");
                hashTimer.Stop();

                var context = new ChainContext {
                    Avatar = avatarObject,
                    Fingerprint = fingerprint,
                    Key = BakeFingerprint.SanitizeKey(avatarObject.scene.name + "__"
                        + BakeFingerprint.NormalizeAvatarName(avatarObject.name)),
                    FingerprintMs = hashTimer.ElapsedMilliseconds,
                };

                foreach (var participant in active) {
                    bool replayed;
                    try {
                        replayed = participant.OnChainStart(context);
                    } catch (Exception e) {
                        Log.Warn("Bake cache chain participant skipped: " + e.Message);
                        continue;
                    }
                    if (!replayed) continue;
                    // Replayed: skip the whole chain; no record, no capture.
                    __result = true;
                    return false;
                }

                state = new ChainState {
                    Context = context,
                    Active = active,
                    Timer = System.Diagnostics.Stopwatch.StartNew(),
                };
                return true;
            } catch (Exception e) {
                Log.Warn("Bake cache chain anchor skipped: " + e.Message);
                state = null;
                return true;
            }
        }

        private static Exception ChainFinalizer(Exception __exception, bool __result) {
            if (chainDepth > 0) chainDepth--;
            if (chainDepth != 0) return __exception;
            var finished = state;
            state = null;
            if (finished == null) return __exception;

            // Only successful chains (no exception AND every callback returned true) are
            // cache candidates — for the telemetry record and the snapshot alike.
            if (__exception != null || !__result) return __exception;

            finished.Timer.Stop();
            finished.Context.ChainSeconds = finished.Timer.Elapsed.TotalSeconds;
            foreach (var participant in finished.Active) {
                try {
                    participant.OnChainSuccess(finished.Context);
                } catch (Exception e) {
                    Log.Warn("Bake cache chain participant failed at chain end: " + e.Message);
                }
            }

            // Parity diagnostic: dump the processed hierarchy after a REAL bake so it can
            // be diffed against the post-replay dump (see BakeCacheReplayPatch).
            if (EditorPrefs.GetBool(Settings.KeyPrefix + "bakeCache.parityDump", false)) {
                try {
                    System.IO.Directory.CreateDirectory(BakeFingerprint.DirPath);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(BakeFingerprint.DirPath, "parity-fresh.txt"),
                        BakeFingerprint.DumpHierarchy(finished.Context.Avatar));
                } catch {
                    // diagnostics only
                }
            }
            return __exception;
        }
    }
}
