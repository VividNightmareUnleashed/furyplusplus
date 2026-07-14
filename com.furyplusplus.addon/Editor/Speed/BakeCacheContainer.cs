using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Root asset for bake-cache sub-asset containers: cloned transient bake outputs
     * (controllers, clips, menus, params, meshes) are attached under one of these via
     * AddObjectToAsset, mirroring NDMF's container pattern — sub-assets carry no
     * file-extension restrictions, so one marker root can hold any object type.
     */
    internal sealed class BakeCacheContainer : ScriptableObject {
    }
}
