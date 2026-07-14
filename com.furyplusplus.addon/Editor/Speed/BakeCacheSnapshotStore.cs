using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * On-disk store for bake-cache snapshots: a fully-processed avatar saved as a prefab plus
     * deep copies of every transient dependency (VRCFury temp assets, NDMF __Generated
     * containers, and memory-only objects from the no-disk-save module), all inside an
     * embedded package NDMF/VRCFury never clean:
     *
     *   Packages/com.furyplusplus.bakecache/Snapshots/<key>/
     *     avatar.prefab  snapshot.json  container-NNN.asset  tex-NNN-*.asset
     *
     * A snapshot is self-describing: replay validity is decided by comparing the live
     * fingerprint against the sidecar's hashes, never against the telemetry records — and it
     * is replaced atomically (sidecar written last; a folder without a sidecar is garbage).
     * Every failure here is a logged refusal that leaves the bake untouched.
     */
    internal static class BakeCacheSnapshotStore {
        /** v2: containers are binary-serialized (text-YAML snapshots load far too slowly). */
        internal const int SnapshotFormatVersion = 2;
        internal const string PackageName = "com.furyplusplus.bakecache";
        internal const string PackageAssetRoot = "Packages/" + PackageName;
        private const string SnapshotsFolder = PackageAssetRoot + "/Snapshots";
        /** NDMF's cap: containers beyond this reimport slowly and risk sub-asset issues. */
        private const int MaxObjectsPerContainer = 256;

        [Serializable]
        internal class SnapshotMeta {
            public int formatVersion;
            public string hierarchyHash;
            public string assetsHash;
            public string configHash;
            public string generatedHash;
            public string addonVersion;
            public string vrcfuryVersion;
            public string vrcfuryMvid;
            public string unityVersion;
            public string createdUtc;
            public double chainSeconds;
            public int clonedObjects;
            public int containerCount;
            public int textureCount;
        }

        internal sealed class Snapshot {
            internal SnapshotMeta Meta;
            internal GameObject Prefab;
            internal string Folder;
        }

        private static bool bootstrapScheduled;
        private static string addonVersion;

        internal static bool IsMounted => AssetDatabase.IsValidFolder(PackageAssetRoot);

        internal static string SnapshotFolder(string key) => SnapshotsFolder + "/" + key;

        /** Pure comparison so the invalidation matrix is unit-testable. */
        internal static bool IsCompatible(SnapshotMeta meta, BakeFingerprint.Result live,
            string vrcfuryVersion, string vrcfuryMvid) {
            return meta != null && live != null
                   && meta.formatVersion == SnapshotFormatVersion
                   && !string.IsNullOrEmpty(meta.hierarchyHash)
                   && meta.hierarchyHash == live.HierarchyHash
                   && meta.assetsHash == live.AssetsHash
                   && meta.configHash == live.ConfigHash
                   && meta.generatedHash == live.GeneratedHash
                   && meta.vrcfuryVersion == vrcfuryVersion
                   && meta.vrcfuryMvid == vrcfuryMvid;
        }

        /** Register the edit-mode bootstrap hooks; called once from the replay module Install. */
        internal static void Init() {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            ScheduleBootstrapIfNeeded();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.EnteredEditMode) ScheduleBootstrapIfNeeded();
        }

        /**
         * Creating an embedded package triggers a UPM resolve, which must never race a bake —
         * so the folder is only ever created from an edit-mode delayCall. When capture wants
         * the package mid-play and it is missing, it skips and this retries on play exit.
         */
        internal static void ScheduleBootstrapIfNeeded() {
            if (IsMounted || bootstrapScheduled) return;
            bootstrapScheduled = true;
            EditorApplication.delayCall += () => {
                bootstrapScheduled = false;
                TryBootstrap();
            };
        }

        private static void TryBootstrap() {
            if (IsMounted) return;
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (BakeCacheReplayModule.Instance?.Enabled != true) return;
            try {
                var dir = Path.GetFullPath(PackageAssetRoot);
                Directory.CreateDirectory(dir);
                var manifest = Path.Combine(dir, "package.json");
                var json = "{\n"
                           + "  \"name\": \"" + PackageName + "\",\n"
                           + "  \"displayName\": \"FuryPlusPlus Bake Cache\",\n"
                           + "  \"version\": \"0.0.0\",\n"
                           + "  \"description\": \"Generated bake-cache snapshots. Safe to delete; rebuilt on the next bake.\",\n"
                           + "  \"hideInEditor\": false\n"
                           + "}\n";
                if (!File.Exists(manifest) || File.ReadAllText(manifest) != json) {
                    File.WriteAllText(manifest, json);
                }
                UnityEditor.PackageManager.Client.Resolve();
            } catch (Exception e) {
                Log.Warn("Bake cache package could not be created: " + e.Message);
            }
        }

        /**
         * Load + validate the snapshot for this key against the live pre-chain fingerprint.
         * Any mismatch or missing artifact is a silent miss (the normal bake just runs).
         */
        internal static bool TryLoad(string key, BakeFingerprint.Result live, out Snapshot snapshot) {
            snapshot = null;
            try {
                var folder = SnapshotFolder(key);
                var sidecar = Path.GetFullPath(folder + "/snapshot.json");
                if (!File.Exists(sidecar)) return false;
                var meta = JsonUtility.FromJson<SnapshotMeta>(File.ReadAllText(sidecar));
                var compat = Bootstrap.Compat;
                if (!IsCompatible(meta, live, compat?.PackageVersion,
                        compat?.ModuleVersionId.ToString())) {
                    return false;
                }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/avatar.prefab");
                if (prefab == null) return false;
                for (var i = 0; i < meta.containerCount; i++) {
                    if (AssetDatabase.LoadMainAssetAtPath($"{folder}/container-{i:000}.asset") == null) {
                        return false;
                    }
                }
                if (meta.textureCount > 0) {
                    var textures = Directory.GetFiles(
                        Path.GetFullPath(folder), "tex-*.asset", SearchOption.TopDirectoryOnly);
                    if (textures.Length < meta.textureCount) return false;
                }
                snapshot = new Snapshot { Meta = meta, Prefab = prefab, Folder = folder };
                return true;
            } catch {
                return false;
            }
        }

        /**
         * Snapshot the fully-processed avatar (called from the chain finalizer after a
         * successful bake). Clones the avatar under an inactive holder (no Awake mid-play),
         * deep-copies the transient dependency closure into cache assets, rewires the clone,
         * saves the prefab, verifies nothing transient survived, and writes the sidecar last.
         */
        internal static bool Capture(GameObject processedAvatar, string key,
            BakeFingerprint.Result fingerprint, double chainSeconds) {
            if (!IsMounted) {
                ScheduleBootstrapIfNeeded();
                Log.Info("Bake cache: cache package not mounted yet; the snapshot will be " +
                         "captured on a later bake.");
                return false;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var phases = new StringBuilder();
            var phaseStartMs = 0L;
            void Phase(string name) {
                phases.Append(name).Append('=')
                    .Append(timer.ElapsedMilliseconds - phaseStartMs).Append("ms ");
                phaseStartMs = timer.ElapsedMilliseconds;
            }
            var folder = SnapshotFolder(key);
            GameObject holder = null;
            try {
                holder = new GameObject("FuryPlusPlus BakeCache Capture") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                holder.SetActive(false);
                var clone = Object.Instantiate(processedAvatar, holder.transform);
                clone.name = processedAvatar.name.Replace("(Clone)", "").Trim();
                StripDontSaveComponents(clone);
                ClearHideFlags(clone);
                Phase("clone");

                var cloneRoot = clone.transform;
                var components = new List<Object>();
                foreach (var component in clone.GetComponentsInChildren<Component>(true)) {
                    // Transforms serialize only parent/children/GO — and the clone root's
                    // m_Father is the capture holder, which must not look like an external ref.
                    if (component != null && !(component is Transform)) components.Add(component);
                }
                var walk = ObjectGraphCloner.Walk(
                    components, obj => Classify(obj, cloneRoot), CanHoldReferences);
                Phase("walk");
                if (walk.HasRejections) {
                    Log.Info("Bake cache: snapshot refused — the processed avatar references " +
                             "objects that cannot be cached: " + DescribeSample(walk.Rejected));
                    return false;
                }

                var map = ObjectGraphCloner.CloneAll(walk.ToClone);
                Phase("copy");
                try {
                    AssetDatabase.DeleteAsset(folder);
                    if (!AssetDatabase.IsValidFolder(SnapshotsFolder)) {
                        AssetDatabase.CreateFolder(PackageAssetRoot, "Snapshots");
                    }
                    AssetDatabase.CreateFolder(SnapshotsFolder, key);

                    var textureCount = 0;
                    var containerCount = 0;
                    AssetDatabase.StartAssetEditing(); // batch imports; Stop flushes them once
                    try {
                        BakeCacheContainer container = null;
                        var inContainer = 0;
                        foreach (var original in walk.ToClone) {
                            var copy = map[original];
                            // Texture2Ds get their own files (NDMF's pattern: big binary blobs
                            // inside shared containers make every container reimport slow).
                            if (copy is Texture2D) {
                                AssetDatabase.CreateAsset(copy,
                                    $"{folder}/tex-{textureCount:000}-{BakeFingerprint.SanitizeKey(copy.name)}.asset");
                                textureCount++;
                                continue;
                            }
                            if (container == null || inContainer >= MaxObjectsPerContainer) {
                                container = ScriptableObject.CreateInstance<BakeCacheContainer>();
                                container.name = $"container-{containerCount:000}";
                                AssetDatabase.CreateAsset(container,
                                    $"{folder}/container-{containerCount:000}.asset");
                                containerCount++;
                                inContainer = 0;
                            }
                            AssetDatabase.AddObjectToAsset(copy, container);
                            inContainer++;
                        }
                    } finally {
                        AssetDatabase.StopAssetEditing();
                    }
                    AssetDatabase.SaveAssets();
                    Phase("persist");

                    var remapTargets = new List<Object>(components);
                    remapTargets.AddRange(map.Values);
                    ObjectGraphCloner.Remap(remapTargets, map, CanHoldReferences);
                    Phase("remap");

                    var prefab = PrefabUtility.SaveAsPrefabAsset(
                        clone, folder + "/avatar.prefab", out var savedOk);
                    if (!savedOk || prefab == null) {
                        throw new InvalidOperationException("prefab save failed");
                    }
                    Phase("prefab");

                    // Verification: every outgoing reference of every saved object must land
                    // on a persisted, non-transient asset (or inside the prefab itself) — a
                    // missed transient would silently break the replayed avatar next session.
                    var verifyRoots = new List<Object>();
                    foreach (var component in prefab.GetComponentsInChildren<Component>(true)) {
                        if (component != null && !(component is Transform)) verifyRoots.Add(component);
                    }
                    verifyRoots.AddRange(map.Values);
                    var leaks = ObjectGraphCloner.Walk(verifyRoots, VerifyClassify, CanHoldReferences);
                    Phase("verify");
                    if (leaks.HasRejections) {
                        AssetDatabase.DeleteAsset(folder);
                        Log.Warn("Bake cache: snapshot discarded — a transient reference " +
                                 "survived capture: " + DescribeSample(leaks.Rejected));
                        return false;
                    }

                    var compat = Bootstrap.Compat;
                    var meta = new SnapshotMeta {
                        formatVersion = SnapshotFormatVersion,
                        hierarchyHash = fingerprint.HierarchyHash,
                        assetsHash = fingerprint.AssetsHash,
                        configHash = fingerprint.ConfigHash,
                        generatedHash = fingerprint.GeneratedHash,
                        addonVersion = AddonVersion,
                        vrcfuryVersion = compat?.PackageVersion,
                        vrcfuryMvid = compat?.ModuleVersionId.ToString(),
                        unityVersion = Application.unityVersion,
                        createdUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        chainSeconds = chainSeconds,
                        clonedObjects = walk.ToClone.Count,
                        containerCount = containerCount,
                        textureCount = textureCount,
                    };
                    File.WriteAllText(Path.GetFullPath(folder + "/snapshot.json"),
                        JsonUtility.ToJson(meta, true));
                    timer.Stop();
                    Log.Info($"Bake cache: captured snapshot for '{clone.name}' in " +
                             $"{timer.Elapsed.TotalSeconds:F1}s ({walk.ToClone.Count} transient " +
                             $"objects, {containerCount} containers, {textureCount} textures; " +
                             $"{phases.ToString().TrimEnd()}).");
                    return true;
                } finally {
                    // Unpersisted leftovers (clones that never made it into an asset) would
                    // otherwise leak into the play session.
                    foreach (var copy in map.Values) {
                        if (copy != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(copy))) {
                            Object.DestroyImmediate(copy);
                        }
                    }
                }
            } catch (Exception e) {
                Log.Warn("Bake cache: snapshot capture failed (bake unaffected): " + e.Message);
                try {
                    AssetDatabase.DeleteAsset(folder);
                } catch {
                    // best-effort partial cleanup
                }
                return false;
            } finally {
                if (holder != null) Object.DestroyImmediate(holder);
            }
        }

        internal static void DeleteSnapshot(Snapshot snapshot) {
            if (snapshot?.Folder != null) AssetDatabase.DeleteAsset(snapshot.Folder);
        }

        /** Settings-window button: drop every snapshot and every fingerprint record/dump. */
        internal static void ClearAll() {
            try {
                AssetDatabase.DeleteAsset(SnapshotsFolder);
                if (Directory.Exists(BakeFingerprint.DirPath)) {
                    Directory.Delete(BakeFingerprint.DirPath, true);
                }
                Log.Info("Bake cache cleared (snapshots and fingerprint records).");
            } catch (Exception e) {
                Log.Warn("Bake cache clear failed: " + e.Message);
            }
        }

        internal static string DescribeSample(IReadOnlyList<Object> objects) {
            var parts = new List<string>();
            for (var i = 0; i < objects.Count && i < 3; i++) {
                var obj = objects[i];
                if (obj == null) continue;
                var path = AssetDatabase.GetAssetPath(obj);
                parts.Add(string.IsNullOrEmpty(path)
                    ? obj.GetType().Name + " '" + obj.name + "'"
                    : path);
            }
            if (objects.Count > 3) parts.Add($"… +{objects.Count - 3} more");
            return string.Join(", ", parts);
        }

        internal static bool IsTransientPath(string path) {
            return path.StartsWith("Packages/com.vrcfury.temp") || path.Contains("/__Generated/");
        }

        /**
         * Meshes and textures hold no serialized object references, but SerializedObject
         * iteration visits their bulk data (vertex bytes, pixels) property by property —
         * skipping them cut the graph passes from ~16s to ~2s on the reference avatar.
         */
        private static bool CanHoldReferences(Object obj) {
            return !(obj is Mesh || obj is Texture);
        }

        private static string AddonVersion {
            get {
                if (addonVersion == null) {
                    try {
                        addonVersion = UnityEditor.PackageManager.PackageInfo
                            .FindForAssembly(typeof(BakeCacheSnapshotStore).Assembly)?.version ?? "unknown";
                    } catch {
                        addonVersion = "unknown";
                    }
                }
                return addonVersion;
            }
        }

        private static ObjectGraphCloner.RefKind Classify(Object obj, Transform cloneRoot) {
            if (obj is GameObject go) {
                if (go.transform.IsChildOf(cloneRoot)) return ObjectGraphCloner.RefKind.AsIs;
                return string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go))
                    ? ObjectGraphCloner.RefKind.Reject   // external scene object
                    : ObjectGraphCloner.RefKind.AsIs;    // prefab asset reference
            }
            if (obj is Component component) {
                if (component.transform.IsChildOf(cloneRoot)) return ObjectGraphCloner.RefKind.AsIs;
                return string.IsNullOrEmpty(AssetDatabase.GetAssetPath(component))
                    ? ObjectGraphCloner.RefKind.Reject
                    : ObjectGraphCloner.RefKind.AsIs;
            }
            var path = AssetDatabase.GetAssetPath(obj);
            // No asset path and not a scene object: an in-memory bake product (the
            // no-disk-save module keeps ALL of VRCFury's output like this).
            if (string.IsNullOrEmpty(path)) return ObjectGraphCloner.RefKind.Clone;
            return IsTransientPath(path)
                ? ObjectGraphCloner.RefKind.Clone
                : ObjectGraphCloner.RefKind.AsIs;
        }

        private static ObjectGraphCloner.RefKind VerifyClassify(Object obj) {
            var path = AssetDatabase.GetAssetPath(obj);
            if (obj is GameObject || obj is Component) {
                // Prefab-internal objects report the prefab's path; a pathless GO/Component
                // here means a scene reference survived into the saved artifacts.
                return string.IsNullOrEmpty(path)
                    ? ObjectGraphCloner.RefKind.Reject
                    : ObjectGraphCloner.RefKind.AsIs;
            }
            if (string.IsNullOrEmpty(path) || IsTransientPath(path)) {
                return ObjectGraphCloner.RefKind.Reject;
            }
            return ObjectGraphCloner.RefKind.AsIs;
        }

        private static void StripDontSaveComponents(GameObject root) {
            foreach (var component in root.GetComponentsInChildren<Component>(true)) {
                if (component == null || component is Transform) continue;
                if ((component.hideFlags & HideFlags.DontSaveInEditor) != 0) {
                    Object.DestroyImmediate(component);
                }
            }
        }

        private static void ClearHideFlags(GameObject root) {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) {
                transform.gameObject.hideFlags = HideFlags.None;
                foreach (var component in transform.GetComponents<Component>()) {
                    if (component != null) component.hideFlags = HideFlags.None;
                }
            }
        }
    }
}
