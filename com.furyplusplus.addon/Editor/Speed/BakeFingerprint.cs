using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Bake-cache fingerprint engine: a conservative hash of everything that could influence a bake
     * (avatar hierarchy + serialized components with stabilized references, content hashes of
     * every referenced asset, build config, VRCFury + FuryPlusPlus versions/settings, NDMF
     * plugin inventory, the installed preprocessor callback list). Consumed by the dry-run
     * telemetry (BakeCacheDryRunModule) and the replay module (BakeCacheReplayModule); the
     * four hashes must stay byte-stable across unchanged bakes, so any change here
     * invalidates every existing fingerprint and snapshot at once.
     */
    internal static class BakeFingerprint {
        internal sealed class Result {
            internal string HierarchyHash;
            internal string AssetsHash;
            internal string ConfigHash;
            internal string GeneratedHash;
            /** The avatar references NDMF/MA-generated or VRCFury-temp assets. */
            internal bool UpstreamGenerated;
            /** Stabilized "scene:<absolute path>" refs to objects OUTSIDE the avatar hierarchy. */
            internal readonly List<string> ExternalSceneRefs = new List<string>();
            /**
             * A snapshot of this avatar could be replayed faithfully: no per-bake generated
             * references (pre-chain those mean leaked state) and no references to scene
             * objects outside the avatar (a prefab round-trip would sever them).
             */
            internal bool ReplayEligible => !UpstreamGenerated && ExternalSceneRefs.Count == 0;
        }

        /** Where fingerprint records and debug dumps live (outside the Unity project). */
        internal static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FuryPlusPlus", "BakeCache");

        private static readonly Regex InstanceRef = new Regex("\\{\"instanceID\":(-?\\d+)\\}");
        private static readonly Regex GuidRef = new Regex(
            "\\{\"fileID\":(-?\\d+),\"guid\":\"([0-9a-f]{32})\",\"type\":\\d+\\}");

        internal static Result Compute(GameObject avatar, string dumpPrefix) {
            var result = new Result();
            var referencedAssetPaths = new SortedSet<string>(StringComparer.Ordinal);
            var generatedAssetPaths = new SortedSet<string>(StringComparer.Ordinal);

            var hierarchy = BuildHierarchy(avatar, result, referencedAssetPaths, generatedAssetPaths);

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
            // Output-relevant config only: cosmetic/pure-speed toggles must not churn the cache.
            config.Append("modules:").Append(ModuleRegistry.DescribeOutputConfig()).Append('\n');
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

            result.HierarchyHash = Sha(hierarchy.ToString());
            result.AssetsHash = Sha(assets.ToString());
            result.ConfigHash = Sha(config.ToString());
            result.GeneratedHash = Sha(generated.ToString());
            return result;
        }

        /**
         * The stabilized hierarchy+components text alone (no hashing, no dump). Used as the
         * parity diagnostic for replayed avatars: dump after a fresh bake and after a replay,
         * diff the two.
         */
        internal static string DumpHierarchy(GameObject avatar) {
            return BuildHierarchy(avatar, new Result(),
                new SortedSet<string>(StringComparer.Ordinal),
                new SortedSet<string>(StringComparer.Ordinal)).ToString();
        }

        private static StringBuilder BuildHierarchy(
            GameObject avatar, Result result,
            SortedSet<string> referencedAssetPaths, SortedSet<string> generatedAssetPaths) {
            var refCache = new Dictionary<long, string>();
            var avatarRoot = avatar.transform;

            string ScenePath(Transform transform) {
                // Under the avatar: relative (the root's name/position vary per initiator).
                for (var walk = transform; walk != null; walk = walk.parent) {
                    if (walk == avatarRoot) return "~/" + HierarchyPath(transform, avatarRoot);
                }
                return HierarchyPath(transform);
            }

            string SceneRef(Transform transform, string suffix) {
                var path = ScenePath(transform);
                var stable = "scene:" + path + suffix;
                if (!path.StartsWith("~/")) result.ExternalSceneRefs.Add(stable);
                return stable;
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
                        if (IsTransientPath(path)) {
                            // Upstream-generated container (NDMF/MA output, VRCFury temp):
                            // sub-asset localIds are random per regeneration, so the
                            // reference is identified by filename only — but the file's
                            // CONTENT still joins a dedicated generated-assets hash, so
                            // regeneration shows up as an honest, precisely-named miss.
                            result.UpstreamGenerated = true;
                            generatedAssetPaths.Add(path);
                            stable = "generated:" + Path.GetFileName(path);
                        } else {
                            referencedAssetPaths.Add(path);
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out long localId);
                            stable = $"asset:{guid}:{localId}";
                        }
                    } else if (obj is GameObject go) {
                        stable = SceneRef(go.transform, "");
                    } else if (obj is Component component) {
                        stable = SceneRef(component.transform, ":" + component.GetType().Name);
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
                        if (IsTransientPath(path)) {
                            result.UpstreamGenerated = true;
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
            return hierarchy;
        }

        internal static string HierarchyPath(Transform transform, Transform root = null) {
            var parts = new List<string>();
            while (transform != null && transform != root) {
                parts.Add(transform.GetSiblingIndex() + ":" + transform.name);
                transform = transform.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /**
         * Per-bake regenerated container (VRCFury temp package, NDMF/MA __Generated output).
         * The ONE definition of "transient": the fingerprint (reference stabilization) and
         * the snapshot store (clone/verify classification) must agree on it for replays to
         * stay faithful.
         */
        internal static bool IsTransientPath(string path) {
            return path.StartsWith("Packages/com.vrcfury.temp") || path.Contains("/__Generated/");
        }

        /** Cache identity of an avatar: play-mode initiators append "(Clone)" to the root. */
        internal static string NormalizeAvatarName(string name) {
            return name.Replace("(Clone)", "").Trim();
        }

        internal static string Sha(string input) {
            using (var sha = SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input)))
                    .Replace("-", "").Substring(0, 32);
            }
        }

        internal static string SanitizeKey(string raw) {
            var builder = new StringBuilder(raw.Length);
            foreach (var c in raw) {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return builder.ToString();
        }
    }
}
