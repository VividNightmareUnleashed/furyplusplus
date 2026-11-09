using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Memoizes the parameter compressor's redundant menu scan, scoped to one
     * ParameterCompressorService.Apply run: GetParamsUsedInMenu fully re-walks the menu tree
     * ~9× per solve (once per candidate menu-type set plus the warning lists). The built
     * name→menuType map is independent of the filter argument, so one walk + cheap per-call
     * filtering is result-identical.
     *
     * Until VRCFury 1.1437.0 this module also kept an exact name set for
     * ControllersService.IsParamUsed. That method is gone: UniqueParamService now answers
     * with a reserved-name set and dictionary lookups on each controller, which leaves
     * nothing worth caching, so that half was removed.
     * Pure memoization: sits below any solver, produces identical decisions.
     */
    internal sealed class CompressorMemoModule : Module<CompressorMemoModule> {

        internal override string Id => "compressorMemo";
        internal override string DisplayName => "Parameter compressor memoization";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Caches the compressor's repeated menu walks within one solve.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            CompressorMemoPatch.Install(harmony, compat);
        }
    }

    internal static class CompressorMemoPatch {
        private sealed class Context {
            internal Dictionary<string, VRCExpressionsMenu.Control.ControlType> MenuMap;
        }

        [ThreadStatic] private static Context active;

        private static FieldInfo solverMenuServiceField;
        private static MethodInfo getReadOnlyMenu;

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
            CompressorCompat.EnsureResolved();
            var apply = ReflectionUtils.Demand(
                CompressorCompat.CompressorApply, "ParameterCompressorService.Apply()");
            var getParamsUsedInMenu = ReflectionUtils.Demand(
                CompressorCompat.SolverGetParamsUsedInMenu,
                "ParameterCompressorSolverService.GetParamsUsedInMenu(...)");

            solverMenuServiceField = ReflectionUtils.Demand(
                CompressorCompat.SolverMenuService, "ParameterCompressorSolverService.menuService");
            getReadOnlyMenu = ReflectionUtils.Demand(
                CompressorCompat.MenuGetReadOnly, "MenuService.GetReadOnlyMenu()");

            harmony.Patch(
                apply,
                prefix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(End))
            );
            harmony.Patch(
                getParamsUsedInMenu,
                prefix: new HarmonyMethod(typeof(CompressorMemoPatch), nameof(GetParamsUsedInMenuPrefix))
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
    }
}
