using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Memoizes the parameter compressor's two redundant scans, scoped to one
     * ParameterCompressorService.Apply run:
     *  1. GetParamsUsedInMenu fully re-walks the menu tree ~9× per solve (once per candidate
     *     menu-type set plus the warning lists). The built name→menuType map is independent
     *     of the filter argument, so one walk + cheap per-call filtering is result-identical.
     *  2. ControllersService.IsParamUsed rescans the params asset and every controller per
     *     probe, once per compressor-created slot/latch parameter. A name set kept EXACT
     *     (additions tracked via _NewParam/AddSyncedParam postfixes) answers in O(1) —
     *     exactness matters because generated SyncIndex/latch names participate in the
     *     PC↔Quest platform alignment.
     * Pure memoization: sits below any solver, produces identical decisions.
     */
    internal sealed class CompressorMemoModule : Module<CompressorMemoModule> {

        internal override string Id => "compressorMemo";
        internal override string DisplayName => "Parameter compressor memoization";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Caches the compressor's repeated menu walks and parameter-name scans within one solve.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorMemoPatch.Install(harmony, compat);
        }
    }

    internal static class CompressorMemoPatch {
        private sealed class Context {
            internal Dictionary<string, VRCExpressionsMenu.Control.ControlType> MenuMap;
            internal HashSet<string> UsedNames;
        }

        [ThreadStatic] private static Context active;

        private static FieldInfo solverMenuServiceField;
        private static MethodInfo getReadOnlyMenu;
        private static FieldInfo controllersParamsServiceField;
        private static MethodInfo getReadOnlyParams;
        private static MethodInfo getAllReadOnlyControllers;
        private static FieldInfo vfControllerCtrlField;

        // Priority order copied from ParameterCompressorSolverService.MenuTypePriority —
        // later entries win ties (the stock replace rule is newIndex >= oldIndex).
        private static readonly VRCExpressionsMenu.Control.ControlType[] MenuTypePriority = {
            VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            VRCExpressionsMenu.Control.ControlType.Toggle,
            VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet,
            VRCExpressionsMenu.Control.ControlType.FourAxisPuppet,
            VRCExpressionsMenu.Control.ControlType.Button,
            VRCExpressionsMenu.Control.ControlType.SubMenu,
        };

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var compressorServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorService"),
                "VF.Service.Compressor.ParameterCompressorService");
            var solverType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorSolverService"),
                "VF.Service.Compressor.ParameterCompressorSolverService");
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            var menuServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.MenuService"), "VF.Service.MenuService");
            var paramsServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ParamsService"), "VF.Service.ParamsService");
            var paramManagerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.ParamManager"), "VF.Utils.ParamManager");
            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"), "VF.Utils.Controller.VFController");

            var apply = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(compressorServiceType, "Apply"),
                "ParameterCompressorService.Apply()");
            var getParamsUsedInMenu = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(solverType, "GetParamsUsedInMenu",
                    method => method.GetParameters().Length == 1),
                "ParameterCompressorSolverService.GetParamsUsedInMenu(...)");
            var isParamUsed = ReflectionUtils.Demand(
                ReflectionUtils.FindMethodWithSignature(controllersServiceType, "IsParamUsed",
                    typeof(bool), typeof(string)),
                "ControllersService.IsParamUsed(string)");
            var newParam = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfControllerType, "_NewParam",
                    method => method.GetParameters().Length == 3),
                "VFController._NewParam(...)");
            var addSyncedParam = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(paramManagerType, "AddSyncedParam",
                    method => method.GetParameters().Length == 1),
                "ParamManager.AddSyncedParam(...)");

            solverMenuServiceField = ReflectionUtils.Demand(
                solverType.GetField("menuService", any), "ParameterCompressorSolverService.menuService");
            getReadOnlyMenu = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(menuServiceType, "GetReadOnlyMenu",
                    method => method.GetParameters().Length == 0),
                "MenuService.GetReadOnlyMenu()");
            controllersParamsServiceField = ReflectionUtils.Demand(
                controllersServiceType.GetField("paramsService", any), "ControllersService.paramsService");
            getReadOnlyParams = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(paramsServiceType, "GetReadOnlyParams",
                    method => method.GetParameters().Length == 0),
                "ParamsService.GetReadOnlyParams()");
            getAllReadOnlyControllers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetAllReadOnlyControllers",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetAllReadOnlyControllers()");
            ClipCurveCompat.EnsureResolved();
            vfControllerCtrlField = ReflectionUtils.Demand(
                ClipCurveCompat.ControllerCtrlField, "VFController.ctrl");

            harmony.Patch(
                apply,
                prefix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(End))
            );
            harmony.Patch(
                getParamsUsedInMenu,
                prefix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(GetParamsUsedInMenuPrefix))
            );
            harmony.Patch(
                isParamUsed,
                prefix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(IsParamUsedPrefix))
            );
            harmony.Patch(
                newParam,
                postfix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(NewParamPostfix))
            );
            harmony.Patch(
                addSyncedParam,
                postfix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(AddSyncedParamPostfix))
            );
        }

        private static void Begin() {
            active = CompressorMemoModule.Instance?.Enabled == true ? new Context() : null;
        }

        private static Exception End(Exception __exception) {
            active = null;
            return __exception;
        }

        // ---- Menu walk memo ----

        private static bool GetParamsUsedInMenuPrefix(
            object __instance,
            ISet<VRCExpressionsMenu.Control.ControlType> allowedMenuTypes,
            ref ISet<string> __result
        ) {
            var context = active;
            if (context == null) return true;

            try {
                if (context.MenuMap == null) {
                    var menuService = solverMenuServiceField.GetValue(__instance);
                    var menu = getReadOnlyMenu.Invoke(menuService, null) as VRCExpressionsMenu;
                    context.MenuMap = BuildMenuMap(menu);
                }
                // Note: stock returns an ImmutableHashSet; every consumer treats the result as
                // an unordered ISet (decision logic uses Contains; warning lists re-sort).
                __result = new HashSet<string>(
                    context.MenuMap
                        .Where(pair => allowedMenuTypes == null || allowedMenuTypes.Contains(pair.Value))
                        .Select(pair => pair.Key)
                );
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn("Compressor memoization fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        /** Exact replication of the stock walk's AttemptToAdd + per-control-type mapping. */
        private static Dictionary<string, VRCExpressionsMenu.Control.ControlType> BuildMenuMap(
            VRCExpressionsMenu rootMenu
        ) {
            var map = new Dictionary<string, VRCExpressionsMenu.Control.ControlType>();

            void AttemptToAdd(VRCExpressionsMenu.Control.Parameter param, VRCExpressionsMenu.Control.ControlType menuType) {
                if (param == null) return;
                if (string.IsNullOrEmpty(param.name)) return;
                var menuTypePriority = Array.IndexOf(MenuTypePriority, menuType);
                if (menuTypePriority < 0) return;
                if (map.TryGetValue(param.name, out var oldMenuType)) {
                    var oldMenuTypePriority = Array.IndexOf(MenuTypePriority, oldMenuType);
                    if (menuTypePriority < oldMenuTypePriority) return;
                }
                map[param.name] = menuType;
            }

            VRCExpressionsMenu.Control.Parameter Sub(VRCExpressionsMenu.Control control, int i) {
                var subs = control.subParameters;
                return subs != null && i < subs.Length ? subs[i] : null;
            }

            var visited = new HashSet<VRCExpressionsMenu>();
            void Walk(VRCExpressionsMenu menu) {
                if (menu == null || !visited.Add(menu) || menu.controls == null) return;
                foreach (var control in menu.controls) {
                    if (control == null) continue;
                    switch (control.type) {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                            AttemptToAdd(control.parameter, VRCExpressionsMenu.Control.ControlType.SubMenu);
                            AttemptToAdd(Sub(control, 0), control.type);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.Button:
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            AttemptToAdd(control.parameter, control.type);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            AttemptToAdd(control.parameter, VRCExpressionsMenu.Control.ControlType.SubMenu);
                            AttemptToAdd(Sub(control, 0), control.type);
                            AttemptToAdd(Sub(control, 1), control.type);
                            AttemptToAdd(Sub(control, 2), control.type);
                            AttemptToAdd(Sub(control, 3), control.type);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            AttemptToAdd(control.parameter, VRCExpressionsMenu.Control.ControlType.SubMenu);
                            AttemptToAdd(Sub(control, 0), control.type);
                            AttemptToAdd(Sub(control, 1), control.type);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            AttemptToAdd(control.parameter, VRCExpressionsMenu.Control.ControlType.SubMenu);
                            Walk(control.subMenu);
                            break;
                    }
                }
            }
            Walk(rootMenu);
            return map;
        }

        // ---- IsParamUsed name-set ----

        private static bool IsParamUsedPrefix(object __instance, string name, ref bool __result) {
            var context = active;
            if (context == null) return true;

            try {
                if (context.UsedNames == null) {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var paramsService = controllersParamsServiceField.GetValue(__instance);
                    if (getReadOnlyParams.Invoke(paramsService, null) is VRCExpressionParameters paramz
                        && paramz.parameters != null) {
                        foreach (var parameter in paramz.parameters) {
                            if (!string.IsNullOrEmpty(parameter?.name)) names.Add(parameter.name);
                        }
                    }
                    var wrappers = (IEnumerable)getAllReadOnlyControllers.Invoke(__instance, null);
                    foreach (var wrapper in wrappers) {
                        if (vfControllerCtrlField.GetValue(wrapper) is AnimatorController controller) {
                            foreach (var parameter in controller.parameters) {
                                if (!string.IsNullOrEmpty(parameter?.name)) names.Add(parameter.name);
                            }
                        }
                    }
                    context.UsedNames = names;
                }
                __result = context.UsedNames.Contains(name);
                return false;
            } catch (Exception e) {
                active = null;
                Log.Warn("Compressor memoization fell back to VRCFury: " + e.Message);
                return true;
            }
        }

        private static void NewParamPostfix(string name) {
            var context = active;
            if (context?.UsedNames != null && !string.IsNullOrEmpty(name)) {
                context.UsedNames.Add(name);
            }
        }

        private static void AddSyncedParamPostfix(object __0) {
            var context = active;
            if (context?.UsedNames == null) return;
            if (__0 is VRCExpressionParameters.Parameter parameter && !string.IsNullOrEmpty(parameter.name)) {
                context.UsedNames.Add(parameter.name);
            }
        }
    }
}
