using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * Preserves VRCFury's renderer-first and controller save passes, then replaces the
     * final scan of every avatar Component with a pass over VrcfObjectFactory's created
     * objects. Controller-only subassets are deliberately left to their controller root;
     * remaining standalone generated assets are saved directly.
     */
    internal sealed class SaveAssetsDiscoveryModule : Module {
        internal static SaveAssetsDiscoveryModule Instance { get; private set; }

        internal static readonly ModuleOption SkipTransformScanOption = new ModuleOption(
            "skipTransformScan",
            "Skip inert Transform scans (experimental)",
            false
        );
        internal static readonly ModuleOption SkipDuplicateRendererScanOption = new ModuleOption(
            "skipDuplicateRendererScan",
            "Skip repeated Renderer scans (experimental)",
            false
        );

        private static readonly ModuleOption[] AllOptions = {
            SkipTransformScanOption,
            SkipDuplicateRendererScanOption
        };

        internal SaveAssetsDiscoveryModule() {
            Instance = this;
        }

        internal override string Id => "saveAssetsDiscovery";
        internal override string DisplayName => "Fast SaveAssets discovery";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Replaces the final every-Component asset scan with a pass over VRCFury's created-object registry.";
        internal override IReadOnlyList<ModuleOption> Options => AllOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            SaveAssetsDuplicateScanPatch.Install(harmony, compat);
        }
    }

    internal static class SaveAssetsDuplicateScanPatch {
        private sealed class ScanSession {
            internal bool SkipTransforms;
            internal bool SkipDuplicates;
            internal bool FastDiscovery;
            internal bool SecondPass;
            internal readonly HashSet<Component> Seen = new HashSet<Component>();
            internal int SkippedTransforms;
            internal int SkippedDuplicates;
            internal int SkippedComponentScans;
            internal int SavedStandaloneRoots;
        }

        // SaveAssetsService.Run does not re-enter, so one active session is enough.
        [ThreadStatic] private static ScanSession scanSession;

        private static MethodInfo saveAssetAndChildren;
        private static FieldInfo createdAssets;

        internal static void Install(Harmony harmony, VrcfuryCompat targets) {
            SaveAssetsCompat.EnsureResolved();
            var run = SaveAssetsCompat.SaveAssetsRun;

            var sessionType = ReflectionUtils.FindType("VF.Utils.SaveAssetsSession");
            var saveComponent = ReflectionUtils.FindMethodWithSignature(
                sessionType,
                "SaveUnsavedComponentAssets",
                typeof(void),
                typeof(Component),
                typeof(string)
            );
            saveAssetAndChildren = ReflectionUtils.FindMethodWithSignature(
                sessionType,
                "SaveAssetAndChildren",
                typeof(void),
                typeof(Object),
                typeof(string),
                typeof(string),
                typeof(bool)
            );

            var factoryType = ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory");
            createdAssets = factoryType?.GetField("created", BindingFlags.Static | BindingFlags.NonPublic);

            if (run == null || saveComponent == null || saveAssetAndChildren == null
                            || createdAssets == null || SaveAssetsCompat.FactoryDidCreate == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                run,
                prefix: new HarmonyMethod(typeof(SaveAssetsDuplicateScanPatch), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(SaveAssetsDuplicateScanPatch), nameof(RunFinalizer))
            );
            harmony.Patch(
                saveComponent,
                prefix: new HarmonyMethod(typeof(SaveAssetsDuplicateScanPatch), nameof(SaveComponentPrefix))
            );
        }

        private static void RunPrefix(out bool __state) {
            // PORT-NOTE: QuickFury's three independent flags map to module Enabled + two
            // ModuleOptions. Literal substitution means the scan-skip options still act while
            // the module itself is disabled, matching QuickFury's independent-flag behavior.
            var session = new ScanSession {
                SkipTransforms = Settings.IsOptionEnabled(
                    SaveAssetsDiscoveryModule.Instance,
                    SaveAssetsDiscoveryModule.SkipTransformScanOption
                ),
                SkipDuplicates = Settings.IsOptionEnabled(
                    SaveAssetsDiscoveryModule.Instance,
                    SaveAssetsDiscoveryModule.SkipDuplicateRendererScanOption
                ),
                FastDiscovery = SaveAssetsDiscoveryModule.Instance?.Enabled == true
            };
            __state = session.SkipTransforms || session.SkipDuplicates || session.FastDiscovery;
            if (__state) scanSession = session;
        }

        private static Exception RunFinalizer(bool __state, Exception __exception) {
            var session = scanSession;
            if (!__state || session == null) return __exception;

            Log.Info(
                $"SaveAssets discovery: skipped {session.SkippedComponentScans} component scans, " +
                $"saved {session.SavedStandaloneRoots} standalone generated roots, skipped " +
                $"{session.SkippedTransforms} Transform scans and {session.SkippedDuplicates} duplicates."
            );
            scanSession = null;
            return __exception;
        }

        private static bool SaveComponentPrefix(object __instance, Component component, string tmpDir) {
            var session = scanSession;
            if (session == null || component == null) return true;
            if (session.FastDiscovery) {
                if (!session.SecondPass && component is Renderer) {
                    // Unity's native collector finds renderer roots without walking the
                    // renderer's large SerializedObject in managed code.
                    try {
                        SaveRendererRoots(session, __instance, (Renderer)component, tmpDir);
                        return false;
                    } catch (Exception e) {
                        Log.Warn(
                            "Fast renderer asset discovery fell back to VRCFury: " + e.Message
                        );
                        return true;
                    }
                }

                if (!session.SecondPass) {
                    session.SecondPass = true;
                    try {
                        SaveRemainingStandaloneRoots(session, __instance, tmpDir);
                    } catch (Exception e) {
                        // Nothing already saved is invalidated by falling back: VRCFury's
                        // normal scan observes the new paths and continues from there.
                        session.FastDiscovery = false;
                        Log.Warn(
                            "Fast generated-asset discovery fell back to VRCFury: " + e.Message
                        );
                        return true;
                    }
                }

                if (session.FastDiscovery) {
                    session.SkippedComponentScans++;
                    return false;
                }
            }

            if (session.SkipTransforms && component is Transform) {
                session.SkippedTransforms++;
                return false;
            }
            if (session.SkipDuplicates && !session.Seen.Add(component)) {
                session.SkippedDuplicates++;
                return false;
            }
            return true;
        }

        private static void SaveRemainingStandaloneRoots(ScanSession session, object saveSession, string tmpDir) {
            var created = createdAssets.GetValue(null) as IEnumerable;
            if (created == null) throw new InvalidOperationException("VRCFury created-object registry is unavailable.");

            var candidates = new List<Object>();
            foreach (var item in created) {
                if (!(item is Object asset) || asset == null) continue;
                if (!CanBeStandaloneRoot(asset)) continue;
                if (PatchUtils.IsPersisted(asset)) continue;
                candidates.Add(asset);
            }

            foreach (var asset in candidates
                         .OrderBy(candidate => candidate.GetType().FullName, StringComparer.Ordinal)
                         .ThenBy(candidate => candidate.name, StringComparer.Ordinal)
                         .ThenBy(candidate => candidate.GetInstanceID())) {
                if (asset == null || PatchUtils.IsPersisted(asset)) continue;

                var filename = GetFilename(asset);
                ReflectionUtils.InvokeUnwrapped(
                    saveAssetAndChildren,
                    saveSession,
                    new object[] { asset, filename, tmpDir, true }
                );
                if (PatchUtils.IsPersisted(asset)) {
                    session.SavedStandaloneRoots++;
                }
            }
        }

        private static void SaveRendererRoots(
            ScanSession session,
            object saveSession,
            Renderer renderer,
            string tmpDir
        ) {
            foreach (var asset in EditorUtility.CollectDependencies(new Object[] { renderer })
                         .Where(asset => asset is Material || asset is Mesh)
                         .Where(asset => asset != null && SaveAssetsCompat.DidCreate(asset))
                         .Distinct()) {
                if (PatchUtils.IsPersisted(asset)) continue;
                ReflectionUtils.InvokeUnwrapped(
                    saveAssetAndChildren,
                    saveSession,
                    new object[] {
                        asset,
                        $"VRCFury {asset.name} - {renderer.gameObject.name}",
                        tmpDir,
                        true
                    }
                );
            }
        }

        private static bool CanBeStandaloneRoot(Object asset) {
            // These objects are meaningful only within an AnimatorController graph. The
            // controller pass has already saved every reachable instance at this point.
            if (asset is AnimatorController
                || asset is AnimatorStateMachine
                || asset is AnimatorState
                || asset is AnimatorTransitionBase
                || asset is StateMachineBehaviour
                || asset is Motion
                || asset is AvatarMask) {
                return false;
            }
            return true;
        }

        private static string GetFilename(Object asset) {
            if (asset.GetType().Name == "VRCExpressionsMenu") return "VRCFury Menu";
            if (asset.GetType().Name == "VRCExpressionParameters") return "VRCFury Params";
            return "VRCFury " + (string.IsNullOrWhiteSpace(asset.name) ? asset.GetType().Name : asset.name);
        }
    }
}
