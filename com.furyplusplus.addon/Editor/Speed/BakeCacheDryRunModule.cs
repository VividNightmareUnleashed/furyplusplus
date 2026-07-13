using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
     * which is exactly what this module measures. The replay stage (instantiate the
     * cached bake, skip the pipeline) lands only after the telemetry shows the
     * fingerprint is trustworthy on the user's own avatars.
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
        internal override string DisplayName => "Bake cache telemetry (dry run, experimental)";
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

        private static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FuryPlusPlus", "BakeCache");

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
                chainDepth = 0;
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
                var record = Fingerprint(avatarObject, "");
                hashTimer.Stop();

                // Initiators rename the processed root (e.g. "(Clone)"); normalize.
                var avatarName = avatarObject.name.Replace("(Clone)", "").Trim();
                pendingKey = SanitizeKey(avatarObject.scene.name + "__" + avatarName);
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

        private static void ChainPrefix(GameObject __0) {
            // Only the outermost call is "the chain"; nothing in the chain is known to
            // recurse into OnPreprocessAvatar, but the guard costs nothing.
            if (chainDepth++ != 0) return;
            chainKey = null;
            chainRecord = null;
            chainTimer = null;
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

                var avatarObject = __0;
                if (avatarObject == null) return;
                // Post-processed rerun (VRCFury tags processed play-mode avatars).
                if (avatarObject.GetComponent(vrcfuryTestType) != null) return;
                // NDMF (HookDedup) and VRCFury (RunPreprocessorsOnlyOncePatch) both dedup
                // repeat chain invocations per play session; mirror that so a suppressed
                // second call can never overwrite the pre-chain fingerprint.
                if (!chainSeen.Add(avatarObject.GetInstanceID())) return;

                var hashTimer = System.Diagnostics.Stopwatch.StartNew();
                var record = Fingerprint(avatarObject, "chain-");
                hashTimer.Stop();

                var avatarName = avatarObject.name.Replace("(Clone)", "").Trim();
                chainKey = SanitizeKey(avatarObject.scene.name + "__" + avatarName) + ".chain";
                chainRecord = record;

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

                chainTimer = System.Diagnostics.Stopwatch.StartNew();
            } catch (Exception e) {
                Log.Warn("Bake cache chain telemetry skipped: " + e.Message);
                chainKey = null;
                chainRecord = null;
            }
        }

        private static Exception ChainFinalizer(Exception __exception, bool __result) {
            if (chainDepth > 0) chainDepth--;
            if (chainDepth != 0) return __exception;
            if (chainKey != null && chainRecord != null) {
                try {
                    if (chainTimer != null) {
                        chainTimer.Stop();
                        chainRecord.lastBakeSeconds = chainTimer.Elapsed.TotalSeconds;
                    }
                    // Persist only successful chains (no exception AND every callback
                    // returned true) as cache candidates.
                    if (__exception == null && __result) {
                        Directory.CreateDirectory(DirPath);
                        File.WriteAllText(
                            Path.Combine(DirPath, chainKey + ".json"),
                            JsonUtility.ToJson(chainRecord, true));
                    }
                } catch (Exception e) {
                    Log.Warn("Bake cache chain telemetry could not persist: " + e.Message);
                }
            }
            chainKey = null;
            chainRecord = null;
            chainTimer = null;
            return __exception;
        }

        // ---- fingerprinting ----

        private static readonly Regex InstanceRef = new Regex("\\{\"instanceID\":(-?\\d+)\\}");
        private static readonly Regex GuidRef = new Regex(
            "\\{\"fileID\":(-?\\d+),\"guid\":\"([0-9a-f]{32})\",\"type\":\\d+\\}");

        private static CacheRecord Fingerprint(GameObject avatar, string dumpPrefix) {
            var referencedAssetPaths = new SortedSet<string>(StringComparer.Ordinal);
            var generatedAssetPaths = new SortedSet<string>(StringComparer.Ordinal);
            var refCache = new Dictionary<long, string>();
            var avatarRoot = avatar.transform;
            var upstreamGenerated = false;

            string ScenePath(Transform transform) {
                // Under the avatar: relative (the root's name/position vary per initiator).
                for (var walk = transform; walk != null; walk = walk.parent) {
                    if (walk == avatarRoot) return "~/" + HierarchyPath(transform, avatarRoot);
                }
                return HierarchyPath(transform);
            }

            string StabilizeReference(long instanceId) {
                if (instanceId == 0) return "null";
                if (refCache.TryGetValue(instanceId, out var cached)) return cached;
                string stable;
                var obj = EditorUtility.InstanceIDToObject((int)instanceId);
                if (obj == null) {
                    stable = "dead";
                } else {
                    var path = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path)) {
                        if (path.Contains("/__Generated/") || path.StartsWith("Packages/com.vrcfury.temp")) {
                            // Upstream-generated container (NDMF/MA output, VRCFury temp):
                            // sub-asset localIds are random per regeneration, so the
                            // reference is identified by filename only — but the file's
                            // CONTENT still joins a dedicated generated-assets hash, so
                            // regeneration shows up as an honest, precisely-named miss.
                            upstreamGenerated = true;
                            generatedAssetPaths.Add(path);
                            stable = "generated:" + Path.GetFileName(path);
                        } else {
                            referencedAssetPaths.Add(path);
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out long localId);
                            stable = $"asset:{guid}:{localId}";
                        }
                    } else if (obj is GameObject go) {
                        stable = "scene:" + ScenePath(go.transform);
                    } else if (obj is Component component) {
                        stable = "scene:" + ScenePath(component.transform) + ":" + component.GetType().Name;
                    } else {
                        stable = "mem:" + obj.GetType().Name + ":" + obj.name;
                    }
                }
                refCache[instanceId] = stable;
                return stable;
            }

            var hierarchy = new StringBuilder();
            var root = avatar.transform;
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true)) {
                // Relative to the avatar root: the root's own name ("(Clone)" suffixes)
                // and scene sibling position vary by play-mode initiator.
                hierarchy.Append(HierarchyPath(transform, root))
                    .Append('|').Append(transform.gameObject.activeSelf ? '1' : '0').Append('\n');
                foreach (var component in transform.GetComponents<Component>()) {
                    if (component == null) {
                        hierarchy.Append("missing-script\n");
                        continue;
                    }
                    if (component is Transform) continue;
                    hierarchy.Append(component.GetType().FullName).Append('#');
                    string json;
                    try {
                        json = EditorJsonUtility.ToJson(component);
                    } catch {
                        json = "unserializable";
                    }
                    json = InstanceRef.Replace(json,
                        match => StabilizeReference(long.Parse(match.Groups[1].Value)));
                    // Persisted refs serialize as fileID+guid directly: collect real
                    // assets for the dependency hash, neutralize per-bake generated ones.
                    json = GuidRef.Replace(json, match => {
                        var guid = match.Groups[2].Value;
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(path)) return match.Value;
                        if (path.Contains("/__Generated/") || path.StartsWith("Packages/com.vrcfury.temp")) {
                            upstreamGenerated = true;
                            generatedAssetPaths.Add(path);
                            return "generated:" + Path.GetFileName(path);
                        }
                        referencedAssetPaths.Add(path);
                        return match.Value;
                    });
                    hierarchy.Append(json);
                    hierarchy.Append('\n');
                }
            }

            var assets = new StringBuilder();
            foreach (var path in referencedAssetPaths) {
                assets.Append(path).Append('#')
                    .Append(AssetDatabase.GetAssetDependencyHash(path).ToString()).Append('\n');
            }

            var generated = new StringBuilder();
            foreach (var path in generatedAssetPaths) {
                generated.Append(path).Append('#')
                    .Append(AssetDatabase.GetAssetDependencyHash(path).ToString()).Append('\n');
            }

            var config = new StringBuilder();
            config.Append("target:").Append(EditorUserBuildSettings.activeBuildTarget).Append('\n');
            config.Append("unity:").Append(Application.unityVersion).Append('\n');
            var compat = Bootstrap.Compat;
            config.Append("vrcfury:").Append(compat?.PackageVersion).Append(':')
                .Append(compat?.ModuleVersionId).Append('\n');
            config.Append("modules:").Append(ModuleRegistry.DescribeStates()).Append('\n');
            // NDMF plugins (Modular Avatar etc.) register through NDMF, not as SDK preprocess
            // callbacks, so the "pre:" list below misses their version changes entirely.
            config.Append("ndmf:").Append(PreprocessChainCompat.DescribeNdmfPlugins()).Append('\n');
            foreach (var callback in TypeCache
                         .GetTypesDerivedFrom<VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback>()
                         .Where(type => !type.IsAbstract)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal)) {
                config.Append("pre:").Append(callback.FullName).Append(':')
                    .Append(callback.Assembly.GetName().Version).Append('\n');
            }

            // Diagnostic dump for chasing fingerprint instability (the whole point of the
            // dry run): set the pref, bake twice, diff the *-prev/*-curr files.
            if (EditorPrefs.GetBool(Settings.KeyPrefix + "bakeCache.debugDump", false)) {
                try {
                    Directory.CreateDirectory(DirPath);
                    foreach (var (name, text) in new[] {
                                 ("hierarchy", hierarchy.ToString()),
                                 ("assets", assets.ToString()),
                                 ("config", config.ToString()),
                                 ("generated", generated.ToString())
                             }) {
                        var current = Path.Combine(DirPath, $"debug-{dumpPrefix}{name}-curr.txt");
                        var previous = Path.Combine(DirPath, $"debug-{dumpPrefix}{name}-prev.txt");
                        if (File.Exists(current)) {
                            File.Copy(current, previous, true);
                        }
                        File.WriteAllText(current, text);
                    }
                } catch {
                    // diagnostics only
                }
            }

            return new CacheRecord {
                hierarchyHash = Sha(hierarchy.ToString()),
                assetsHash = Sha(assets.ToString()),
                configHash = Sha(config.ToString()),
                generatedHash = Sha(generated.ToString()),
                upstreamGenerated = upstreamGenerated
            };
        }

        private static string HierarchyPath(Transform transform, Transform root = null) {
            var parts = new List<string>();
            while (transform != null && transform != root) {
                parts.Add(transform.GetSiblingIndex() + ":" + transform.name);
                transform = transform.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Sha(string input) {
            using (var sha = SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input)))
                    .Replace("-", "").Substring(0, 32);
            }
        }

        private static string SanitizeKey(string raw) {
            var builder = new StringBuilder(raw.Length);
            foreach (var c in raw) {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return builder.ToString();
        }
    }
}
