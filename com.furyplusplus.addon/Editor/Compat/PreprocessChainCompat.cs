using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Lazy compat holder for the SDK preprocess-chain anchor and the (optional) NDMF surface
     * the bake-cache telemetry reads. OnPreprocessAvatar is the fail-closed member (callers
     * Demand it at Install); everything NDMF-related is fail-soft because NDMF is a peer
     * package that may legitimately be absent — absence just means an empty inventory.
     */
    internal static class PreprocessChainCompat {
        private static bool resolved;

        /**
         * static bool VRCBuildPipelineCallbacks.OnPreprocessAvatar(GameObject) — the single
         * entry every bake initiator funnels through: the SDK upload pipeline, NDMF
         * apply-on-play (ApplyOnPlay.MaybeProcessAvatar calls it directly), VRCFury's own
         * PlayModeTrigger, and Av3Emu's "run preprocess hooks" mode. VRCFury itself Harmony-
         * patches this exact method (RunPreprocessorsOnlyOncePatch), so it is a proven-stable
         * patch target.
         */
        internal static MethodInfo OnPreprocessAvatar { get; private set; }

        private static string ndmfPluginInventory;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            OnPreprocessAvatar = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks"),
                "OnPreprocessAvatar",
                typeof(bool),
                typeof(GameObject));
        }

        /**
         * Stable one-line inventory of registered NDMF plugins. Modular Avatar and friends
         * register through NDMF's assembly-level [ExportsPlugin] attribute rather than as
         * IVRCSDKPreprocessAvatarCallback, so the SDK callback list alone misses their
         * version changes. The MVID pins the exact compiled plugin build (assembly Version
         * is often a static 0.0.0). Cached per domain load — the plugin set cannot change
         * without a domain reload. Empty string when NDMF is not installed.
         */
        internal static string DescribeNdmfPlugins() {
            if (ndmfPluginInventory != null) return ndmfPluginInventory;

            var entries = new SortedSet<string>(StringComparer.Ordinal);
            var attributeType = ReflectionUtils.FindType("nadena.dev.ndmf.ExportsPlugin");
            var pluginTypeProperty = attributeType?.GetProperty(
                "PluginType", BindingFlags.Instance | BindingFlags.Public);
            if (attributeType != null && pluginTypeProperty != null) {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    object[] attributes;
                    try {
                        attributes = assembly.GetCustomAttributes(attributeType, false);
                    } catch {
                        continue; // dynamic/reflection-only assemblies can refuse attribute reads
                    }
                    foreach (var attribute in attributes) {
                        if (!(pluginTypeProperty.GetValue(attribute) is Type pluginType)) continue;
                        entries.Add(pluginType.FullName
                                    + ":" + pluginType.Assembly.GetName().Version
                                    + ":" + pluginType.Assembly.ManifestModule.ModuleVersionId);
                    }
                }
            }

            var builder = new StringBuilder();
            foreach (var entry in entries) {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(entry);
            }
            ndmfPluginInventory = builder.ToString();
            return ndmfPluginInventory;
        }
    }
}
