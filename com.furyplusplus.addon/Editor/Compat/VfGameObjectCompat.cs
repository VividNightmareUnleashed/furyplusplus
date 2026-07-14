using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Lazy holder for VF.Utils.VFGameObject and its _gameObject backing field — the
     * wrapper→GameObject unwrap shared by every module that receives VRCFury's avatar
     * handle. Members stay null on resolution failure; consumers Demand from Install().
     */
    internal static class VfGameObjectCompat {
        private static bool resolved;

        internal static System.Type VfGameObjectType;
        internal static FieldInfo GameObjectField; // VFGameObject._gameObject

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;
            VfGameObjectType = ReflectionUtils.FindType("VF.Utils.VFGameObject");
            GameObjectField = VfGameObjectType?.GetField("_gameObject",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(GameObjectField, "VFGameObject._gameObject");
        }

        /** Null-safe unwrap; null when the wrapper (or the field) is unavailable. */
        internal static GameObject Unwrap(object vfGameObject) {
            if (vfGameObject == null || GameObjectField == null) return null;
            return GameObjectField.GetValue(vfGameObject) as GameObject;
        }
    }
}
