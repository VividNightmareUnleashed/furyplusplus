using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * VFController performs an Array.Find and a full parameters-array copy for every
     * parameter it creates. Keep an exact name index during the build and use Unity's
     * native AddParameter API, while invalidating around the handful of bulk rewrites.
     */
    internal sealed class ControllerParameterIndexModule : Module<ControllerParameterIndexModule> {

        internal override string Id => "controllerParameterIndex";
        internal override string DisplayName => "Controller parameter index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "O(1) animator-parameter lookups during the build instead of per-call array scans.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            ControllerParameterIndexPatch.Install(harmony, compat);
        }
    }

    internal static class ControllerParameterIndexPatch {
        private sealed class Entry {
            internal AnimatorController Controller;
            internal readonly Dictionary<string, AnimatorControllerParameter> ByName =
                new Dictionary<string, AnimatorControllerParameter>(StringComparer.Ordinal);
        }

        [ThreadStatic] private static Dictionary<int, Entry> active;
        private static FieldInfo controllerField;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var type = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            ClipCurveCompat.EnsureResolved();
            controllerField = ClipCurveCompat.ControllerCtrlField;
            var getParam = ReflectionUtils.FindMethodWithSignature(
                type,
                "GetParam",
                typeof(AnimatorControllerParameter),
                typeof(string)
            );
            var newParam = ReflectionUtils.FindUniqueMethod(
                type,
                "_NewParam",
                method => method.ReturnType == typeof(AnimatorControllerParameter)
                          && method.GetParameters().Length == 3
            );
            // SetDefault mutates the parameter instance returned by GetParam. Stock
            // VRCFury gets a fresh marshalled copy there, so the write never reaches the
            // controller; invalidating drops the indexed instance so later lookups match.
            var mutators = type?
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => new[] {
                    "TakeOwnershipOf", "RemoveParameter", "RemoveInvalidParameters",
                    "RewriteParameters", "UpgradeWrongParamTypes", "set_parameters",
                    "SetDefault"
                }.Contains(method.Name))
                .ToArray() ?? Array.Empty<MethodInfo>();

            if (controllerField == null || getParam == null || newParam == null || mutators.Length < 8) {
                throw new InvalidOperationException("target signature mismatch");
            }

            harmony.Patch(
                compatibility.RunMain,
                prefix: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(End))
            );
            harmony.Patch(
                getParam,
                prefix: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(GetParam))
            );
            harmony.Patch(
                newParam,
                prefix: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(NewParam))
            );
            foreach (var mutator in mutators) {
                harmony.Patch(
                    mutator,
                    prefix: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(Invalidate)),
                    postfix: new HarmonyMethod(typeof(ControllerParameterIndexPatch), nameof(Invalidate))
                );
            }
        }

        private static void Begin() {
            active = ControllerParameterIndexModule.Instance?.Enabled == true
                ? new Dictionary<int, Entry>()
                : null;
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        private static bool GetParam(object __instance, string name, ref AnimatorControllerParameter __result) {
            var entry = GetEntry(__instance);
            if (entry == null) return true;
            entry.ByName.TryGetValue(name, out __result);
            return false;
        }

        private static bool NewParam(
            object __instance,
            string name,
            AnimatorControllerParameterType type,
            Action<AnimatorControllerParameter> with,
            ref AnimatorControllerParameter __result
        ) {
            var entry = GetEntry(__instance);
            if (entry == null) return true;
            if (entry.ByName.TryGetValue(name, out __result)) return false;

            var parameter = new AnimatorControllerParameter { name = name, type = type };
            with?.Invoke(parameter);
            entry.Controller.AddParameter(parameter);
            entry.ByName[name] = parameter;
            __result = parameter;
            return false;
        }

        private static void Invalidate(object __instance) {
            var context = active;
            if (context == null || __instance == null) return;
            var controller = controllerField.GetValue(__instance) as AnimatorController;
            if (controller != null) context.Remove(controller.GetInstanceID());
        }

        private static Entry GetEntry(object wrapper) {
            var context = active;
            if (context == null || wrapper == null) return null;
            var controller = controllerField.GetValue(wrapper) as AnimatorController;
            if (controller == null) return null;
            var id = controller.GetInstanceID();
            if (context.TryGetValue(id, out var existing)) return existing;

            var created = new Entry { Controller = controller };
            foreach (var parameter in controller.parameters) {
                if (parameter != null && !created.ByName.ContainsKey(parameter.name)) {
                    created.ByName.Add(parameter.name, parameter);
                }
            }
            context.Add(id, created);
            return created;
        }
    }
}
