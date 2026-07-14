using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Root asset for bake-cache sub-asset containers: cloned transient bake outputs
     * (controllers, clips, menus, params, meshes) are attached under one of these via
     * AddObjectToAsset, mirroring NDMF's container pattern — sub-assets carry no
     * file-extension restrictions, so one marker root can hold any object type.
     *
     * PreferBinarySerialization is load-bearing (also NDMF's choice for its containers):
     * as ForceText YAML a full-avatar snapshot is ~90 MB and Unity parses it synchronously
     * during avatar init on every replayed play — measured ~11s of the play transition.
     */
    [PreferBinarySerialization]
    internal sealed class BakeCacheContainer : ScriptableObject {
    }
}
