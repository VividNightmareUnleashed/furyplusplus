using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FuryPlusPlus {
    /**
     * VFGameObject.Destroy normally discovers every PhysBone, PhysBone collider, and
     * contact in the upload root again for every pruned Armature Link object. This patch
     * snapshots those three ordered component sequences immediately before the first prune
     * and reuses them while preserving VRCFury's per-object filtering and destruction order.
     */
    internal sealed class ArmatureDestroyIndexModule : Module<ArmatureDestroyIndexModule> {

        internal override string Id => "armatureDestroyIndex";
        internal override string DisplayName => "Armature destroy index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Armature & links";
        internal override string Description =>
            "Snapshots dynamics components once per phase instead of re-scanning per pruned object.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ArmatureCompat.EnsureResolved();
            ArmatureDestroyIndexPatch.Install(harmony, compat);
        }
    }

    internal static class ArmatureDestroyIndexPatch {
        private sealed class CategoryTarget {
            internal Type ComponentType;
            internal MethodInfo GetRootTransform;
        }

        private sealed class IndexedComponent {
            internal int Order;
            internal Component Component;
            internal GameObject UploadRoot;
        }

        private sealed class Context {
            internal GameObject Avatar;
            internal List<GameObject> UploadRoots;
            internal List<Dictionary<int, List<IndexedComponent>>> CategoryIndexes;
        }

        [ThreadStatic] private static Context active;

        private static MethodInfo getUploadRoots;
        private static MethodInfo destroyConstraint;
        private static CategoryTarget[] categoryTargets;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var constraintType = ReflectionUtils.FindType("VF.Utils.VFConstraint");

            var destroy = ReflectionUtils.FindNoArgVoid(ArmatureCompat.VfGameObjectType, "Destroy");
            getUploadRoots = ArmatureCompat.VfGameObjectType?
                .GetProperty("uploadRoots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetGetMethod(true);
            destroyConstraint = ReflectionUtils.FindNoArgVoid(constraintType, "Destroy");

            categoryTargets = new[] {
                CreateCategoryTarget("VRC.Dynamics.VRCPhysBoneBase"),
                CreateCategoryTarget("VRC.Dynamics.VRCPhysBoneColliderBase"),
                CreateCategoryTarget("VRC.Dynamics.ContactBase")
            };

            if (!ArmatureCompat.ArmatureLinkAvailable || destroy == null
                || ArmatureCompat.GetConstraintsMethod == null || constraintType == null
                || getUploadRoots == null || destroyConstraint == null
                || categoryTargets.Any(target => target == null)) {
                categoryTargets = null;
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                ArmatureCompat.ArmatureLinkApply,
                prefix: new HarmonyMethod(typeof(ArmatureDestroyIndexPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ArmatureDestroyIndexPatch), nameof(End))
            );
            harmony.Patch(
                destroy,
                prefix: new HarmonyMethod(typeof(ArmatureDestroyIndexPatch), nameof(Destroy))
            );
        }

        private static CategoryTarget CreateCategoryTarget(string typeName) {
            var componentType = ReflectionUtils.FindType(typeName);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) return null;

            var root = ReflectionUtils.FindMethodWithSignature(
                componentType,
                "GetRootTransform",
                typeof(Transform)
            );
            if (root == null) return null;

            return new CategoryTarget {
                ComponentType = componentType,
                GetRootTransform = root
            };
        }

        private static void Begin(object __instance) {
            active = null;
            if (ArmatureDestroyIndexModule.Instance?.Enabled != true) return;

            try {
                var avatar = ArmatureCompat.GetAvatar(__instance, ArmatureCompat.ArmatureLinkAvatarField);
                if (avatar == null) return;
                active = new Context { Avatar = avatar };
            } catch (Exception e) {
                Log.Warn("Destroy index fell back to VRCFury: " + e.Message);
            }
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        private static bool Destroy(object __instance) {
            var context = active;
            if (context == null) return true;

            var targetObject = ArmatureCompat.GetGameObject(__instance);
            if (targetObject == null || context.Avatar == null
                                     || !targetObject.transform.IsChildOf(context.Avatar.transform)) {
                return true;
            }

            try {
                var uploadRoots = ReadUploadRoots(__instance);
                if (uploadRoots == null || uploadRoots.Any(root => root == null)) return true;

                if (context.UploadRoots == null) {
                    BuildIndex(context, uploadRoots);
                } else if (!SameRoots(context.UploadRoots, uploadRoots)) {
                    // A different upload-root set is outside the cache's exactness boundary.
                    return true;
                }
            } catch (Exception e) {
                active = null;
                Log.Warn("Destroy index fell back to VRCFury: " + e.Message);
                return true;
            }

            var target = targetObject.transform;
            // The destroyed subtree is invariant across the three component categories.
            var subtree = target.GetComponentsInChildren<Transform>(true);
            foreach (var categoryIndex in context.CategoryIndexes) {
                var matches = new List<IndexedComponent>();
                foreach (var child in subtree) {
                    if (categoryIndex.TryGetValue(child.GetInstanceID(), out var bucket)) {
                        matches.AddRange(bucket);
                    }
                }
                matches.Sort((left, right) => left.Order.CompareTo(right.Order));
                foreach (var entry in matches) {
                    var component = entry.Component;
                    // A fresh GetComponentsInChildren call would no longer include destroyed
                    // components or components that have left this upload root.
                    if (component == null || entry.UploadRoot == null
                                          || !component.transform.IsChildOf(entry.UploadRoot.transform)) continue;
                    Object.DestroyImmediate(component);
                }
            }

            var constraints = ReflectionUtils.InvokeUnwrapped(
                ArmatureCompat.GetConstraintsMethod,
                __instance,
                new object[] { false, true }
            ) as IEnumerable;
            if (constraints == null) {
                throw new InvalidOperationException("VRCFury GetConstraints returned a non-enumerable result.");
            }
            foreach (var constraint in constraints) {
                ReflectionUtils.InvokeUnwrapped(destroyConstraint, constraint, null);
            }

            Object.DestroyImmediate(targetObject);
            return false;
        }

        private static List<GameObject> ReadUploadRoots(object vfGameObject) {
            var roots = ReflectionUtils.InvokeUnwrapped(getUploadRoots, vfGameObject, null) as IEnumerable;
            if (roots == null) return null;

            var output = new List<GameObject>();
            foreach (var root in roots) {
                output.Add(ArmatureCompat.GetGameObject(root));
            }
            return output;
        }

        private static void BuildIndex(Context context, List<GameObject> uploadRoots) {
            var indexes = new List<Dictionary<int, List<IndexedComponent>>>(categoryTargets.Length);
            foreach (var target in categoryTargets) {
                var byComponentRoot = new Dictionary<int, List<IndexedComponent>>();
                var order = 0;
                foreach (var uploadRoot in uploadRoots) {
                    foreach (var component in uploadRoot.GetComponentsInChildren(target.ComponentType, true)) {
                        if (component == null) continue;
                        var root = ReflectionUtils.InvokeUnwrapped(
                            target.GetRootTransform,
                            component,
                            null
                        ) as Transform;
                        if (root == null) continue;
                        byComponentRoot.GetOrAddList(root.GetInstanceID()).Add(new IndexedComponent {
                            Order = order++,
                            Component = component,
                            UploadRoot = uploadRoot
                        });
                    }
                }
                indexes.Add(byComponentRoot);
            }

            context.UploadRoots = new List<GameObject>(uploadRoots);
            context.CategoryIndexes = indexes;
        }

        private static bool SameRoots(IReadOnlyList<GameObject> left, IReadOnlyList<GameObject> right) {
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++) {
                if (left[i] != right[i]) return false;
            }
            return true;
        }
    }
}
