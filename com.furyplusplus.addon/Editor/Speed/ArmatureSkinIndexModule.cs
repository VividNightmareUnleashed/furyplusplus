using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Replaces Armature Link's per-bone skin mutation with one chronological replay per
     * skin. Records the transforms at each call and commits before VRCFury reads skin
     * state to name linked roots or prune unused bones.
     */
    internal sealed class ArmatureSkinIndexModule : Module<ArmatureSkinIndexModule> {

        internal override string Id => "armatureSkinIndex";
        internal override string DisplayName => "Batched armature skin rewrite";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Armature & links";
        internal override string Description =>
            "Commits Armature Link's bone and bindpose rewrites once per skin instead of once per bone.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ArmatureCompat.EnsureResolved();
            ArmatureSkinIndexPatch.Install(harmony, compat);
        }
    }

    internal static class ArmatureSkinIndexPatch {
        private sealed class Rewrite {
            internal Transform From;
            internal Transform To;
            internal Matrix4x4 BindposeDelta;
        }

        private sealed class Context {
            internal GameObject Avatar;
            internal readonly List<Rewrite> Rewrites = new List<Rewrite>();
        }

        [ThreadStatic] private static Context active;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            // Both root naming and pruning must see the latest bone references.
            ArmatureCompat.DemandArmatureLink();
            ReflectionUtils.Demand(ArmatureCompat.RewriteSkins, "ArmatureLinkService.RewriteSkins(...)");
            ReflectionUtils.Demand(ArmatureCompat.GetRootName, "ArmatureLinkService.GetRootName(...)");
            ReflectionUtils.Demand(ArmatureCompat.GetUsageReasons, "ArmatureLinkService.GetUsageReasons(...)");
            ReflectionUtils.Demand(ArmatureCompat.GetMutableMesh, "RendererExtensions.GetMutableMesh(...)");
            ReflectionUtils.Demand(ArmatureCompat.Dirty, "DirtyUtils.Dirty(Object)");

            harmony.Patch(
                ArmatureCompat.ArmatureLinkApply,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(End))
            );
            harmony.Patch(
                ArmatureCompat.RewriteSkins,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(RecordRewrite))
            );
            harmony.Patch(
                ArmatureCompat.GetRootName,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(Flush))
            );
            harmony.Patch(
                ArmatureCompat.GetUsageReasons,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(Flush))
            );
        }

        private static void Begin(object __instance) {
            active = null;
            if (ArmatureSkinIndexModule.Instance?.Enabled != true) return;

            try {
                var avatar = ArmatureCompat.GetAvatar(__instance, ArmatureCompat.ArmatureLinkAvatarField);
                if (avatar == null) return;
                active = new Context { Avatar = avatar };
            } catch (Exception e) {
                Log.Warn("Batched skin rewrite fell back to VRCFury: " + e.Message);
            }
        }

        private static Exception End(Exception __exception) {
            try {
                // Bones recorded after the last GetRootName still have to land.
                Flush();
            } catch (Exception e) {
                Log.Warn("Batched skin rewrite fell back to VRCFury: " + e.Message);
            } finally {
                active = null;
            }
            return __exception;
        }

        private static bool RecordRewrite(object __0, object __1) {
            var context = active;
            if (context == null) return true;

            var from = ArmatureCompat.GetGameObject(__0)?.transform;
            var to = ArmatureCompat.GetGameObject(__1)?.transform;
            if (from == null || to == null) return true;

            context.Rewrites.Add(new Rewrite {
                From = from,
                To = to,
                // Capture this now. Later Armature Links can align a parent and change
                // from.localToWorldMatrix before the batch is committed.
                BindposeDelta = to.worldToLocalMatrix * from.localToWorldMatrix
            });
            return false;
        }

        /**
         * Commits what has been recorded so far and keeps batching for the rest of Apply.
         * Clearing the list is required for correctness, not just tidiness: BindposeDelta is
         * applied multiplicatively, so replaying an already-committed rewrite would compound it.
         */
        private static void Flush() {
            var context = active;
            if (context == null) return;

            if (context.Avatar == null || context.Rewrites.Count == 0) return;

            foreach (var skin in context.Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                if (skin == null) continue;
                RewriteSkin(skin, context.Rewrites);
            }
            context.Rewrites.Clear();
        }

        private static void RewriteSkin(SkinnedMeshRenderer skin, IReadOnlyList<Rewrite> rewrites) {
            var bones = skin.bones;
            if (bones == null || bones.Length == 0) return;

            var slotsByBone = new Dictionary<Transform, List<int>>();
            for (var i = 0; i < bones.Length; i++) {
                var bone = bones[i];
                if (bone != null) slotsByBone.GetOrAddList(bone).Add(i);
            }

            Mesh mesh = null;
            Matrix4x4[] bindposes = null;
            var changed = false;

            foreach (var rewrite in rewrites) {
                if (rewrite.From == null || rewrite.To == null) continue;
                if (!slotsByBone.TryGetValue(rewrite.From, out var slots) || slots.Count == 0) continue;

                if (!changed) {
                    mesh = ReflectionUtils.InvokeUnwrapped(ArmatureCompat.GetMutableMesh, null, new object[] {
                        skin,
                        "Needed to change bone bind-poses for Armature Link to re-use bones on base armature"
                    }) as Mesh;
                    bindposes = mesh?.bindposes;
                    changed = true;
                }

                foreach (var slot in slots) {
                    if (bindposes != null && slot < bindposes.Length) {
                        bindposes[slot] = rewrite.BindposeDelta * bindposes[slot];
                    }
                    bones[slot] = rewrite.To;
                }

                slotsByBone.Remove(rewrite.From);
                slotsByBone.GetOrAddList(rewrite.To).AddRange(slots);
            }

            if (!changed) return;

            if (mesh != null && bindposes != null) {
                // Enumerable.Zip in VRCFury truncates to the shorter of bones and bindposes
                // on the first rewrite. Preserve that unusual edge case exactly.
                var count = Math.Min(bones.Length, bindposes.Length);
                if (bindposes.Length != count) Array.Resize(ref bindposes, count);
                mesh.bindposes = bindposes;
            }

            skin.bones = bones;
            ReflectionUtils.InvokeUnwrapped(ArmatureCompat.Dirty, null, new object[] { skin });
        }
    }
}
