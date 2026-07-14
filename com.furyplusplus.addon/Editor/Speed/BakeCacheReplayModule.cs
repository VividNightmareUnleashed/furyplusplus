using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * Bake-cache replay: on a whole-chain fingerprint HIT, skip the entire NDMF+VRCFury
     * preprocessor chain and restore the avatar in place from the cached processed snapshot.
     * Play-mode only, never uploads. This module installs no Harmony patch of its own — the
     * shared chain anchor lives in BakeCacheDryRunPatch (one fingerprint computation serves
     * telemetry and replay); Install fail-closes if that anchor is not installed.
     *
     * Failure posture: everything before the first destructive touch of the avatar falls
     * open to a normal bake; a failure after that point is loud, still skips the chain
     * (baking a half-restored avatar is worse), and deletes the snapshot so it cannot recur.
     */
    internal sealed class BakeCacheReplayModule : Module {
        internal static BakeCacheReplayModule Instance { get; private set; }

        internal static readonly ModuleOption CaptureOnly = new ModuleOption(
            "captureOnly",
            "Capture snapshots but never replay (validation)",
            false,
            "Builds and refreshes snapshots on every successful bake and logs when a replay " +
            "would have happened, without ever restoring one. Turn this on for a few days to " +
            "validate the cache against your own avatars before trusting replays.");

        private static readonly ModuleOption[] options = { CaptureOnly };

        internal BakeCacheReplayModule() {
            Instance = this;
        }

        internal override string Id => "bakeCacheReplay";
        internal override string DisplayName => "Bake cache replay (experimental)";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override CompatTier RequiredTier => CompatTier.ExactVersion;
        internal override bool DefaultEnabled => false;
        internal override IReadOnlyList<ModuleOption> Options => options;
        internal override string Description =>
            "EXPERIMENTAL. When nothing that could influence the bake has changed since the " +
            "last successful play-mode bake (verified by the bake-cache fingerprint), restores " +
            "the avatar from a cached snapshot of the processed result and skips the entire " +
            "NDMF+VRCFury preprocessor chain. The restore itself takes well under a second, " +
            "but total play entry is still dominated by Unity's normal scene/avatar startup — " +
            "expect meaningfully faster, not instant. The bigger win is that the CPU-heavy " +
            "bake (mesh baking, animator merging, compressor solving, asset writes) simply " +
            "never runs, so entering play mode needs far less CPU/memory/disk — most " +
            "noticeable on weaker PCs or while in VR. Snapshots live in " +
            "Packages/com.furyplusplus.bakecache (safe to delete). Uploads are never cached. " +
            "Not available for avatars that reference scene objects outside their own " +
            "hierarchy; AudioLink's play-mode refresh is skipped on replayed bakes.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            var dryRun = ModuleRegistry.Find("bakeCacheDryRun");
            if (dryRun == null || !ModuleRegistry.IsActive(dryRun)) {
                throw new MissingMemberException(
                    "requires the bake-cache telemetry module (shared preprocess-chain anchor)");
            }
            NdmfCompat.EnsureResolved(); // fail-soft: absence is fine, resolution is not risky
            BakeCacheSnapshotStore.Init();
        }

        internal override string ReportStats() => BakeCacheReplayPatch.DescribeStats();
    }

    internal static class BakeCacheReplayPatch {
        private static int replays;
        private static double savedSeconds;

        internal static string DescribeStats() {
            return replays == 0
                ? null
                : "replays=" + replays + " savedSeconds="
                  + savedSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /**
         * Restore the avatar in place from the snapshot. Returns true when the preprocessor
         * chain must be skipped — a successful replay OR a mid-restore failure (running the
         * chain over a half-restored avatar would only deepen the damage). Returns false only
         * while nothing destructive has happened yet, so the caller can fall through to a
         * completely normal bake.
         *
         * The initiators hold the `obj` reference and rename/inspect it after the call, so
         * the avatar object itself must survive: children and components are transplanted
         * onto it rather than swapping the GameObject.
         */
        internal static bool TryReplay(GameObject obj, BakeCacheSnapshotStore.Snapshot snapshot) {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            GameObject holder = null;
            var destructive = false;
            var keepBar = false;
            try {
                BakeCacheSnapshotStore.Progress(
                    $"Loading cached bake for '{obj.name}'…", 0.15f);
                // Validate and materialize everything BEFORE touching obj.
                holder = new GameObject("FuryPlusPlus BakeCache Replay") {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (obj.scene.IsValid()) {
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, obj.scene);
                }
                holder.SetActive(false);
                var shell = Object.Instantiate(snapshot.Prefab, holder.transform);
                if (shell == null || shell.GetComponentsInChildren<Transform>(true).Length < 1) {
                    throw new InvalidOperationException("snapshot prefab instantiated empty");
                }
                var shellLayer = shell.layer;
                var shellTag = shell.tag;

                BakeCacheSnapshotStore.Progress("Restoring avatar from cache…", 0.55f);
                // ---- first destructive op; failures below are torn-avatar territory ----
                destructive = true;
                obj.SetActive(false); // no Awake/OnEnable until references are consistent again

                for (var i = obj.transform.childCount - 1; i >= 0; i--) {
                    Object.DestroyImmediate(obj.transform.GetChild(i).gameObject);
                }
                DestroyRootComponents(obj);

                var shellRoot = shell.transform;
                while (shellRoot.childCount > 0) {
                    shellRoot.GetChild(0).SetParent(obj.transform, false);
                }

                // Rebuild root components via claim-or-add: AddComponent may auto-add
                // RequireComponent dependencies (VRCAvatarDescriptor pulls PipelineManager);
                // the pass after it must claim those instead of duplicating them.
                var map = new Dictionary<Object, Object> {
                    { shell, obj },
                    { shellRoot, obj.transform },
                };
                var claimed = new HashSet<Component>();
                foreach (var source in shell.GetComponents<Component>()) {
                    if (source == null) continue; // missing script: nothing restorable
                    if (source is Transform) continue;
                    Component destination = null;
                    foreach (var existing in obj.GetComponents(source.GetType())) {
                        if (existing != null && !claimed.Contains(existing)) {
                            destination = existing;
                            break;
                        }
                    }
                    if (destination == null) destination = obj.AddComponent(source.GetType());
                    if (destination == null) {
                        throw new InvalidOperationException(
                            "could not restore component " + source.GetType().Name);
                    }
                    claimed.Add(destination);
                    EditorUtility.CopySerialized(source, destination);
                    map[source] = destination;
                }

                // Children and pasted components may still reference the shell root or its
                // components; remap every component under obj onto the restored equivalents.
                // Transforms are excluded: SetParent already rewired their parent/children
                // pointers, and writing those through SerializedObject would corrupt them.
                var targets = new List<Object>();
                foreach (var component in obj.GetComponentsInChildren<Component>(true)) {
                    if (component != null && !(component is Transform)) targets.Add(component);
                }
                ObjectGraphCloner.Remap(targets, map);

                obj.layer = shellLayer;
                try {
                    obj.tag = shellTag;
                } catch (UnityException) {
                    // undefined tag in this project; layer/tag are cosmetic for play mode
                }
                obj.SetActive(true);
                Object.DestroyImmediate(holder);
                holder = null;

                NdmfCompat.TryMarkProcessed(obj);
                timer.Stop();
                replays++;
                savedSeconds += snapshot.Meta.chainSeconds;
                Log.Info($"Bake cache: REPLAYED '{obj.name}' in {timer.Elapsed.TotalSeconds:F2}s " +
                         $"(skipped ~{snapshot.Meta.chainSeconds:F1}s bake).");
                // Leave the bar covering the rest of the silent play transition (scene and
                // avatar startup); the play-mode-change hook clears it once play begins.
                BakeCacheSnapshotStore.Progress("Cache restored — starting play mode…", 0.95f);
                keepBar = true;

                if (EditorPrefs.GetBool(Settings.KeyPrefix + "bakeCache.parityDump", false)) {
                    try {
                        Directory.CreateDirectory(BakeFingerprint.DirPath);
                        File.WriteAllText(Path.Combine(BakeFingerprint.DirPath, "parity-replay.txt"),
                            BakeFingerprint.DumpHierarchy(obj));
                    } catch {
                        // diagnostics only
                    }
                }
                return true;
            } catch (Exception e) {
                if (!destructive) {
                    Log.Warn("Bake cache: replay validation failed; running the normal bake: "
                             + e.Message);
                    if (holder != null) Object.DestroyImmediate(holder);
                    return false;
                }
                Log.Error("Bake cache: replay failed MID-RESTORE — the avatar in this play " +
                          "session is torn; exit play mode. The snapshot has been deleted so " +
                          "this cannot recur. " + e);
                try {
                    BakeCacheSnapshotStore.DeleteSnapshot(snapshot);
                } catch {
                    // best effort; TryLoad hash checks still gate any future use
                }
                if (holder != null) Object.DestroyImmediate(holder);
                return true; // running the chain over a half-restored avatar is worse
            } finally {
                if (!keepBar) EditorUtility.ClearProgressBar();
            }
        }

        /**
         * Destroy every non-Transform component on the root in RequireComponent dependency
         * order (dependents first), so Unity never vetoes with a console error mid-restore.
         */
        private static void DestroyRootComponents(GameObject obj) {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);
            var remaining = new List<Component>();
            foreach (var component in obj.GetComponents<Component>()) {
                if (component == null || component is Transform) continue;
                // NDMF may be inside GetOrAddComponent for its activator right now (the
                // replay fires from that component's Awake); it self-destructs on its own.
                if (NdmfCompat.IsNdmfActivator(component)) continue;
                remaining.Add(component);
            }
            for (var pass = 0; remaining.Count > 0 && pass < 16; pass++) {
                var required = new HashSet<Type>();
                foreach (var component in remaining) {
                    foreach (var attribute in component.GetType()
                                 .GetCustomAttributes(typeof(RequireComponent), true)) {
                        var require = (RequireComponent)attribute;
                        if (require.m_Type0 != null) required.Add(require.m_Type0);
                        if (require.m_Type1 != null) required.Add(require.m_Type1);
                        if (require.m_Type2 != null) required.Add(require.m_Type2);
                    }
                }
                var destroyedAny = false;
                for (var i = remaining.Count - 1; i >= 0; i--) {
                    var component = remaining[i];
                    var blocked = false;
                    foreach (var type in required) {
                        if (type.IsInstanceOfType(component)) {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) continue;
                    Object.DestroyImmediate(component);
                    remaining.RemoveAt(i);
                    destroyedAny = true;
                }
                if (!destroyedAny) break; // mutual requirement: fall through to force pass
            }
            for (var i = remaining.Count - 1; i >= 0; i--) {
                if (remaining[i] != null) Object.DestroyImmediate(remaining[i]);
            }
        }
    }
}
