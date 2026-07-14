using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    internal static class PatchUtils {
        internal static List<TItem> GetOrAddList<TKey, TItem>(
            this Dictionary<TKey, List<TItem>> dictionary,
            TKey key
        ) {
            if (!dictionary.TryGetValue(key, out var list)) {
                list = new List<TItem>();
                dictionary.Add(key, list);
            }
            return list;
        }

        internal static bool IsPersisted(Object asset) {
            return !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset));
        }

        /**
         * Best-effort table patching (profiler and progress-pump target tables): a
         * missing or unpatchable method logs one warning and costs one site, never a
         * broken module. `failVerb` prefixes the warning, e.g. "Could not profile".
         */
        internal static void PatchAllBestEffort(
            Harmony harmony,
            IEnumerable<(string TypeName, string[] MethodNames)> targets,
            HarmonyMethod prefix,
            HarmonyMethod finalizer,
            string failVerb
        ) {
            foreach (var (typeName, methodNames) in targets) {
                foreach (var methodName in methodNames) {
                    foreach (var method in ReflectionUtils.FindDeclaredMethods(typeName, methodName)) {
                        try {
                            harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                        } catch (Exception e) {
                            Log.Warn($"{failVerb} {typeName}.{methodName}: {e.Message}");
                        }
                    }
                }
            }
        }
    }
}
