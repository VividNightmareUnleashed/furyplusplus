using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * HasDpsOrTpsMaterial can force Unity to load and introspect every shader used by
     * the avatar. Cache the boolean for persistent, clean materials using their GUID,
     * local file id and full dependency hash. Changes to a material or shader produce a
     * different key; generated or dirty materials always use VRCFury's live probe.
     * All persisted results share one LRU-trimmed EditorPrefs entry, so the cache stays
     * bounded across material churn and a future generation bump only has to delete a
     * single known key.
     */
    internal sealed class SpsMaterialProbeCacheModule : Module {
        internal static SpsMaterialProbeCacheModule Instance { get; private set; }

        internal SpsMaterialProbeCacheModule() {
            Instance = this;
        }

        internal override string Id => "spsMaterialProbeCache";
        internal override string DisplayName => "SPS material probe cache";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Caches DPS/TPS material probe results for clean persistent materials across bakes.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            SpsMaterialProbeCachePatch.Install(harmony, compat);
        }
    }

    internal static class SpsMaterialProbeCachePatch {
        private const string MapPrefKey = "com.furyplusplus.spsProbe.map.v2";
        // Roughly one signature per renderer per avatar state; 512 entries keep several
        // avatars warm while the serialized map stays under ~20 KB.
        private const int MaxEntries = 512;

        private static readonly Dictionary<string, Hash128> DependencyHashes =
            new Dictionary<string, Hash128>(StringComparer.Ordinal);
        // In-memory mirror of the persisted map, loaded once per domain so repeated
        // probes never read the registry.
        private static SpsProbeResultMap ResultsByKey;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var type = ReflectionUtils.FindType("VF.Builder.Haptics.TpsConfigurer");
            var target = ReflectionUtils.FindMethodWithSignature(
                type,
                "HasDpsOrTpsMaterial",
                typeof(bool),
                typeof(Renderer)
            );
            if (target == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(SpsMaterialProbeCachePatch), nameof(GetCached)),
                postfix: new HarmonyMethod(typeof(SpsMaterialProbeCachePatch), nameof(Store))
            );
            // The signature is only self-invalidating while the dependency hashes are
            // current; a shader or material edit between two bakes must be observed.
            // Flushing after the bake persists the burst of new results in one write.
            harmony.Patch(
                compatibility.RunMain,
                prefix: new HarmonyMethod(typeof(SpsMaterialProbeCachePatch), nameof(InvalidateDependencyHashes)),
                postfix: new HarmonyMethod(typeof(SpsMaterialProbeCachePatch), nameof(FlushResults))
            );
        }

        private static void InvalidateDependencyHashes() {
            DependencyHashes.Clear();
        }

        private static bool GetCached(Renderer r, ref bool __result, out string __state) {
            __state = null;
            if (SpsMaterialProbeCacheModule.Instance?.Enabled != true || r == null) return true;

            try {
                var signature = BuildSignature(r.sharedMaterials);
                if (signature == null) return true;
                var key = Hash128.Compute(signature).ToString();
                // The signature key is content-derived, so the loaded map stays valid
                // across bakes and saves a registry read per repeated probe.
                EnsureLoaded();
                if (ResultsByKey.TryGet(key, out var cached)) {
                    __result = cached;
                    return false;
                }
                __state = key;
                return true;
            } catch {
                // Persistence is an optional fast path; VRCFury remains authoritative.
                return true;
            }
        }

        private static void Store(string __state, bool __result) {
            if (string.IsNullOrEmpty(__state) || ResultsByKey == null) return;
            ResultsByKey.Set(__state, __result);
        }

        private static void EnsureLoaded() {
            if (ResultsByKey != null) return;
            // PORT-NOTE: QuickFury called PurgeLegacyPrefs() here to delete its historic
            // per-signature "com.quickfury.spsProbe.v1." EditorPrefs entries (tracked by
            // "com.quickfury.spsProbe.v1Purged"). Those keys never existed under the
            // com.furyplusplus namespace, so the entire v1-legacy purge block was dropped.
            ResultsByKey = SpsProbeResultMap.Deserialize(
                EditorPrefs.GetString(MapPrefKey, ""),
                MaxEntries
            );
            // The bake postfix covers the normal path; these cover bakes that throw
            // before the postfix and recency-only updates still pending at reload.
            AssemblyReloadEvents.beforeAssemblyReload += FlushResults;
            EditorApplication.quitting += FlushResults;
        }

        private static void FlushResults() {
            if (ResultsByKey == null || !ResultsByKey.Dirty) return;
            try {
                EditorPrefs.SetString(MapPrefKey, ResultsByKey.Serialize());
            } catch {
                // Persistence is an optional fast path; VRCFury remains authoritative.
            }
        }

        private static string BuildSignature(Material[] materials) {
            var builder = new StringBuilder();
            builder.Append(Application.unityVersion).Append('|');
            foreach (var material in materials) {
                if (material == null) {
                    builder.Append("null;");
                    continue;
                }
                if (EditorUtility.IsDirty(material)) return null;

                var path = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(path)) return null;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        material,
                        out var guid,
                        out long localId
                    )) return null;

                if (!DependencyHashes.TryGetValue(path, out var dependencyHash)) {
                    dependencyHash = AssetDatabase.GetAssetDependencyHash(path);
                    DependencyHashes.Add(path, dependencyHash);
                }
                builder.Append(guid).Append(':').Append(localId).Append(':')
                    .Append(dependencyHash).Append(';');
            }
            return builder.ToString();
        }
    }
}
