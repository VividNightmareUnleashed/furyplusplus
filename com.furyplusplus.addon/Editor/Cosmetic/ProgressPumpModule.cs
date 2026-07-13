using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Keeps the liquid progress bar animating while VRCFury is deep inside a long phase.
     * A synchronous bake blocks the editor loop, so between VRCFury's own Progress()
     * calls nothing on screen can repaint. VRCFury already solves visibility at phase
     * boundaries by invoking the internal EditorWindow.RepaintImmediately() (see
     * VRCFProgressWindow.RepaintNow); this module applies the same mechanism INSIDE the
     * phases: throttled prefixes on VRCFury's action dispatcher and a curated set of its
     * hottest internals repaint the themed progress window at most 20x/second.
     *
     * Hot internals are patched best-effort exactly like the detailed profiler targets —
     * a missing method just means fewer pump sites, never a broken module. Pumping skips
     * while a RenderTexture is active (VRCFury documents a Unity segfault in
     * RepaintImmediately under that condition and clears it before RunMain; mask/mesh
     * baking can set one mid-phase) and disables itself for the rest of the bake on the
     * first repaint failure. Phases that never hit a pump site still freeze — with a
     * blocked main thread that part is physics.
     */
    internal sealed class ProgressPumpModule : Module {
        internal static ProgressPumpModule Instance { get; private set; }

        internal ProgressPumpModule() {
            Instance = this;
        }

        internal override string Id => "progressPump";
        internal override string DisplayName => "Animate progress bar during long phases";
        internal override ModuleKind Kind => ModuleKind.Cosmetic;
        internal override CompatTier RequiredTier => CompatTier.Profiling;
        internal override string Description =>
            "Repaints the build progress window from inside VRCFury's long-running phases " +
            "(at most 20x/second) so the liquid fill keeps animating instead of freezing " +
            "until the next progress step.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ProgressPumpPatch.Install(harmony, compat);
        }

        internal override string ReportStats() {
            var pumps = ProgressPumpPatch.RepaintsThisWindow;
            return pumps > 0 ? $"midPhaseRepaints={pumps}" : null;
        }
    }

    internal static class ProgressPumpPatch {
        internal static int RepaintsThisWindow;

        private static long minIntervalTicks;
        private static long lastPumpTimestamp;
        private static int mainThreadId;
        private static bool pumping;
        private static WeakReference<EditorWindow> target;
        private static MethodInfo repaintImmediately;

        /**
         * Same best-effort table style as the detailed profiler targets: moderately hot
         * methods spanning the long phases — armature links/moves, layer optimization,
         * mutable-asset clone rewrites, haptics baking, and the save tail.
         */
        private static readonly (string TypeName, string[] MethodNames)[] PumpTargets = {
            ("VF.Service.LayerToTreeService", new[] { "OptimizeLayer", "GetBindingsAnimatedInLayer" }),
            ("VF.Service.ObjectMoveService", new[] { "Move" }),
            ("VF.Service.ArmatureLinkService", new[] { "ApplyOne" }),
            ("VF.Service.FindAnimatedTransformsService", new[] { "Find" }),
            ("VF.Utils.MutableManager", new[] { "RewriteInternals" }),
            ("VF.Utils.VRCFuryAssetDatabase", new[] { "SaveAsset", "AttachAsset" }),
            ("VF.Utils.SaveAssetsSession", new[] { "SaveAssetAndChildren" }),
            ("VF.Inspector.VRCFuryHapticSocketEditor", new[] { "Bake" }),
            ("VF.Builder.MeshBaker", new[] { "BakeMesh" }),
        };

        internal static void Install(Harmony harmony, VrcfuryCompat compat) {
            repaintImmediately = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(typeof(EditorWindow), "RepaintImmediately"),
                "EditorWindow.RepaintImmediately()");
            minIntervalTicks = Stopwatch.Frequency / 20;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var pump = new HarmonyMethod(typeof(ProgressPumpPatch), nameof(PumpPrefix));
            harmony.Patch(compat.ActionCall, prefix: pump);
            foreach (var (typeName, methodNames) in PumpTargets) {
                foreach (var methodName in methodNames) {
                    foreach (var method in ReflectionUtils.FindDeclaredMethods(typeName, methodName)) {
                        try {
                            harmony.Patch(method, prefix: pump);
                        } catch (Exception e) {
                            Log.Warn($"Progress pump skipped {typeName}.{methodName}: {e.Message}");
                        }
                    }
                }
            }
        }

        /**
         * Called by the progress window theme when it creates a liquid-themed window —
         * a phase boundary, so the EditorPrefs-backed Enabled check happens here and
         * the hot prefix below only ever looks at statics.
         */
        internal static void RegisterWindow(EditorWindow window) {
            var module = ProgressPumpModule.Instance;
            if (module == null || !ModuleRegistry.IsActive(module) || !module.Enabled) return;
            RepaintsThisWindow = 0;
            lastPumpTimestamp = 0;
            target = new WeakReference<EditorWindow>(window);
        }

        private static void PumpPrefix() {
            var reference = target;
            if (reference == null || pumping) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            var now = Stopwatch.GetTimestamp();
            if (now - lastPumpTimestamp < minIntervalTicks) return;
            lastPumpTimestamp = now;
            // VRCFury clears the active RenderTexture before RunMain because
            // RepaintImmediately can segfault with one live; mid-phase bakes can set one.
            if (RenderTexture.active != null) return;
            try {
                pumping = true;
                if (!reference.TryGetTarget(out var window) || window == null) {
                    target = null; // window closed/destroyed — bake over
                    return;
                }
                repaintImmediately.Invoke(window, null);
                RepaintsThisWindow++;
            } catch {
                target = null; // one repaint failure ends pumping for this bake
            } finally {
                pumping = false;
            }
        }
    }
}
