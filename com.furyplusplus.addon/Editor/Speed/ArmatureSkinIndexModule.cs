using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Replaces Armature Link's per-bone skin mutation with one chronological replay per
     * skin. VRCFury normally clones and assigns the same bones/bindposes arrays thousands
     * of times; this records the exact transforms at each call and commits each skin once
     * immediately before deferred hierarchy moves are applied.
     */
    internal sealed class ArmatureSkinIndexModule : Module {
        internal static ArmatureSkinIndexModule Instance { get; private set; }

        internal ArmatureSkinIndexModule() {
            Instance = this;
        }

        internal override string Id => "armatureSkinIndex";
        internal override string DisplayName => "Batched armature skin rewrite";
        internal override ModuleKind Kind => ModuleKind.Speed;
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

        private static MethodInfo getMutableMesh;
        private static MethodInfo dirty;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var armatureType = ReflectionUtils.FindType("VF.Service.ArmatureLinkService");
            var rendererExtensions = ReflectionUtils.FindType("VF.Utils.RendererExtensions");
            var dirtyUtils = ReflectionUtils.FindType("VF.Utils.DirtyUtils");

            var rewriteSkins = ReflectionUtils.FindUniqueMethod(
                armatureType,
                "RewriteSkins",
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 3
            );
            getMutableMesh = ReflectionUtils.FindMethodWithSignature(
                rendererExtensions,
                "GetMutableMesh",
                typeof(Mesh),
                typeof(Renderer),
                typeof(string)
            );
            dirty = ReflectionUtils.FindMethodWithSignature(
                dirtyUtils,
                "Dirty",
                typeof(void),
                typeof(UnityEngine.Object)
            );

            // PORT-NOTE: ApplyDeferred lived on QuickFury's VrcfuryCompatibility; VrcfuryCompat
            // has no such member, so resolve it here with the same predicates as QuickFury's
            // VrcfuryCompatibility.TryCreate.
            var objectMoveServiceType = ReflectionUtils.FindType("VF.Service.ObjectMoveService");
            var applyDeferred = ReflectionUtils.FindUniqueMethod(
                objectMoveServiceType,
                "ApplyDeferred",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 0
            );

            if (!ArmatureCompat.ArmatureLinkAvailable || rewriteSkins == null
                || applyDeferred == null || getMutableMesh == null || dirty == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                ArmatureCompat.ArmatureLinkApply,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(End))
            );
            harmony.Patch(
                rewriteSkins,
                prefix: new HarmonyMethod(typeof(ArmatureSkinIndexPatch), nameof(RecordRewrite))
            );
            harmony.Patch(
                applyDeferred,
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
            active = null;
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

        private static void Flush() {
            var context = active;
            active = null;
            if (context == null) return;

            if (context.Avatar == null || context.Rewrites.Count == 0) return;

            foreach (var skin in context.Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                if (skin == null) continue;
                RewriteSkin(skin, context.Rewrites);
            }
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
                    mesh = ReflectionUtils.InvokeUnwrapped(getMutableMesh, null, new object[] {
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
            ReflectionUtils.InvokeUnwrapped(dirty, null, new object[] { skin });
        }
    }
}
