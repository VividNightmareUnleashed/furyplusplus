using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * ObjectMoveService.Move rebuilds the complete humanoid immovable-bone set for
     * every move. Armature Link performs thousands of deferred moves against one
     * avatar, so build that invariant set once and preserve the original reparent,
     * safe-name, path-recording and PhysBone-exclusion behavior directly.
     */
    internal sealed class FastArmatureMoveModule : Module<FastArmatureMoveModule> {

        internal override string Id => "fastArmatureMove";
        internal override string DisplayName => "Fast Armature Link moves";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Armature & links";
        internal override string Description =>
            "Builds the immovable-bone set once per Armature Link instead of once per deferred move.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ArmatureCompat.EnsureResolved();
            FastArmatureMovePatch.Install(harmony, compat);
        }
    }

    internal static class FastArmatureMovePatch {
        private sealed class Context {
            internal GameObject Avatar;
            internal readonly HashSet<int> Immovable = new HashSet<int>();
            internal object DeferredService;
            internal IList Deferred;
        }

        [ThreadStatic] private static Context active;

        private static FieldInfo deferredMovesField;

        internal static void Install(Harmony harmony, VrcfuryCompat targets) {
            var moveType = ReflectionUtils.FindType("VF.Service.ObjectMoveService");
            var move = ReflectionUtils.FindUniqueMethod(
                moveType,
                "Move",
                method => method.ReturnType == typeof(void) && method.GetParameters().Length == 5
            );
            // PORT-NOTE: DeferredMoves lived on QuickFury's VrcfuryCompatibility; VrcfuryCompat
            // has no such member, so resolve the field here exactly as QuickFury's
            // VrcfuryCompatibility.TryCreate did.
            deferredMovesField = moveType?
                .GetField("deferred", BindingFlags.Instance | BindingFlags.NonPublic);

            if (!ArmatureCompat.ArmatureLinkAvailable || move == null
                || ArmatureCompat.RemoveFromPhysbones == null || deferredMovesField == null) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                ArmatureCompat.ArmatureLinkApply,
                prefix: new HarmonyMethod(typeof(FastArmatureMovePatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(FastArmatureMovePatch), nameof(End))
            );
            harmony.Patch(
                move,
                prefix: new HarmonyMethod(typeof(FastArmatureMovePatch), nameof(Move))
            );
        }

        private static void Begin(object __instance) {
            active = null;
            if (FastArmatureMoveModule.Instance?.Enabled != true) return;

            try {
                var avatar = ArmatureCompat.GetAvatar(__instance, ArmatureCompat.ArmatureLinkAvatarField);
                if (avatar == null) return;

                var context = new Context { Avatar = avatar };
                context.Immovable.Add(avatar.transform.GetInstanceID());
                var animator = avatar.GetComponent<Animator>();
                if (animator != null && animator.isHuman) {
                    for (var i = 0; i < (int)HumanBodyBones.LastBone; i++) {
                        var bone = (HumanBodyBones)i;
                        if (bone == HumanBodyBones.LeftEye || bone == HumanBodyBones.RightEye) continue;
                        var current = animator.GetBoneTransform(bone);
                        while (current != null && current != avatar.transform) {
                            context.Immovable.Add(current.GetInstanceID());
                            current = current.parent;
                        }
                    }
                }
                active = context;
            } catch (Exception e) {
                active = null;
                Log.Warn("Fast Armature Link moves fell back to VRCFury: " + e.Message);
            }
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        private static bool Move(
            object __instance,
            object __0,
            object __1,
            string __2,
            bool __3,
            bool __4
        ) {
            var context = active;
            // Armature Link always defers; retain VRCFury for any unexpected immediate move.
            if (context == null || !__4) return true;

            GameObject obj;
            GameObject newParent;
            IList deferred;
            try {
                obj = ArmatureCompat.GetGameObject(__0);
                newParent = ArmatureCompat.GetGameObject(__1);
                if (obj == null || context.Avatar == null) return true;
                deferred = GetDeferred(context, __instance);
                if (deferred == null) return true;
            } catch (Exception e) {
                active = null;
                Log.Warn("Fast Armature Link moves fell back to VRCFury: " + e.Message);
                return true;
            }

            if (context.Immovable.Contains(obj.transform.GetInstanceID())) {
                // Deliberately outside the fallback scope: this must reach VRCFury's caller
                // exactly like the stock immovable-object error.
                throw new Exception(
                    $"VRCFury is trying to move the {obj.name} object, but bones / root avatar objects cannot be moved." +
                    " You are probably trying to do something weird in one of your VRCFury components. Don't do that."
                );
            }

            var mutated = false;
            try {
                var oldPath = AnimationUtility.CalculateTransformPath(
                    obj.transform,
                    context.Avatar.transform
                );
                mutated = true;
                if (newParent != null) obj.transform.SetParent(newParent.transform, __3);
                if (__2 != null) obj.name = __2;
                EnsureAnimationSafeName(obj.transform);
                var newPath = AnimationUtility.CalculateTransformPath(
                    obj.transform,
                    context.Avatar.transform
                );

                ReflectionUtils.InvokeUnwrapped(
                    ArmatureCompat.RemoveFromPhysbones,
                    null,
                    new object[] { __0, true }
                );
                deferred.Add((oldPath, newPath));
                return false;
            } catch (Exception e) {
                // Once hierarchy state changed, running VRCFury's method again would
                // record the wrong old path. Fail loudly instead of double-applying.
                if (mutated) throw;
                active = null;
                Log.Warn("Fast Armature Link moves fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        // The service instance and its deferred list are stable for the whole Apply, so
        // avoid a reflection field read on every one of the thousands of moves.
        private static IList GetDeferred(Context context, object service) {
            if (!ReferenceEquals(context.DeferredService, service)) {
                context.Deferred = deferredMovesField.GetValue(service) as IList;
                context.DeferredService = service;
            }
            return context.Deferred;
        }

        private static void EnsureAnimationSafeName(Transform transform) {
            var name = transform.name.Replace("/", "_");
            if (string.IsNullOrEmpty(name)) name = "_";
            var parent = transform.parent;
            if (parent != null) {
                for (var i = 0; ; i++) {
                    var finalName = name + (i == 0 ? "" : $" ({i})");
                    var existing = parent.Find(finalName);
                    if (existing != null && existing != transform) continue;
                    name = finalName;
                    break;
                }
            }
            transform.name = name;
        }
    }
}
