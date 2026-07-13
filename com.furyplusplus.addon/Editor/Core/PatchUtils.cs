using System.Collections.Generic;
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
    }
}
