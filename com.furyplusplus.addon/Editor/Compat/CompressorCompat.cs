using System;
using System.Linq;
using System.Reflection;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for the VF.Service.Compressor members shared by the compressor modules
     * (lane packing, solver, eligibility widening, sub-8-bit lanes). First EnsureResolved
     * pays; consuming modules Demand what they need in Install() (fail-closed).
     */
    internal static class CompressorCompat {
        private static bool resolved;

        internal static Type DecisionType;                    // OptimizationDecision
        internal static FieldInfo DecisionNumberSlots;
        internal static FieldInfo DecisionBoolSlots;
        internal static FieldInfo DecisionUseBadPriority;
        internal static FieldInfo DecisionCompress;           // IList<VRCExpressionParameters.Parameter>
        internal static MethodInfo DecisionGetBatches;        // () → (List<List<P>>, List<List<P>>)
        internal static MethodInfo DecisionGetBatchCount;
        internal static MethodInfo DecisionGetFinalCost;      // (int originalCost) → int
        internal static MethodInfo DecisionGetIndexBitCount;  // () → int
        internal static MethodInfo DecisionOptimize;          // (int originalCost)
        internal static FieldInfo BatchesItem1;               // ValueTuple field: numberBatches
        internal static FieldInfo BatchesItem2;               // ValueTuple field: boolBatches

        internal static Type SolverType;                      // ParameterCompressorSolverService
        internal static MethodInfo SolverPublicSolve;         // GetParamsToOptimize()
        internal static MethodInfo SolverPrivateSolve;        // GetParamsToOptimize(paramz, types, addDriven, cost, bad)
        internal static MethodInfo SolverGetParamsUsedInMenu; // (ISet<ControlType>) → ISet<string>
        internal static FieldInfo SolverParamsService;
        internal static FieldInfo SolverControllers;
        internal static FieldInfo SolverExcService;
        internal static FieldInfo SolverMenuService;

        internal static Type SolverOutputType;                // ParameterCompressorSolverOutput
        internal static FieldInfo OutputDecision;
        internal static FieldInfo OutputOptions;
        internal static Type SelectionOptionsType;            // ParamSelectionOptions
        internal static FieldInfo OptionsAllowedMenuTypes;

        internal static Type CompressorServiceType;           // ParameterCompressorService
        internal static MethodInfo CompressorApply;           // Apply()
        internal static Type LayerServiceType;                // ParameterCompressorLayerService
        internal static Type ControllerManagerType;           // VF.Utils.ControllerManager
        internal static MethodInfo LayerBuildLayer;           // BuildLayer(OptimizationDecision, ControllerManager)

        internal static MethodInfo ParamsGetReadOnly;         // ParamsService.GetReadOnlyParams()
        internal static MethodInfo GetMaxCost;                // VRCExpressionParametersExtensions.GetMaxCost()
        internal static MethodInfo ParamsClone;               // VRCExpressionParametersExtensions.Clone(paramz)

        // VRCFury 1.1372.0 deleted GetAllReadOnlyControllers (it re-loaded every controller from
        // its asset on each call). GetAllUsedControllers is the successor VRCFury moved its own
        // IsParamUsed onto, and it yields ControllerManager (a VFController), so the parameter
        // and layer surface both consumers need is reachable straight off the wrapper.
        internal static MethodInfo ControllersGetAll;         // ControllersService.GetAllUsedControllers()
        internal static MethodInfo ControllersIsParamUsed;
        internal static FieldInfo ControllersParamsService;
        internal static MethodInfo MenuGetReadOnly;
        internal static MethodInfo ControllerNewParam;
        internal static PropertyInfo ControllerParameters;
        internal static MethodInfo ParamManagerAddSynced;
        internal static PropertyInfo ControllerLayers;
        internal static MethodInfo LayerGetDrivers;
        internal static MethodInfo ControllerManagerOne;
        internal static MethodInfo VfaApName;
        internal static MethodInfo FactoryCreate;

        internal static MethodInfo CompressorMenuItemGet;     // CompressorMenuItem.Get()

        internal static MethodInfo ClipSetAap;                // AnimationClipExtensions.SetAap(clip, string, FloatOrObjectCurve)
        internal static MethodInfo FloatToCurve;              // FloatOrObjectCurve.op_Implicit(float)
        internal static MethodInfo MakeAap;                   // ControllerManager.MakeAap(string, float, bool)

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;

            DecisionType = ReflectionUtils.FindType("VF.Service.Compressor.OptimizationDecision");
            if (DecisionType != null) {
                DecisionNumberSlots = DecisionType.GetField("numberSlots", any);
                DecisionBoolSlots = DecisionType.GetField("boolSlots", any);
                DecisionUseBadPriority = DecisionType.GetField("useBadPriorityMethod", any);
                DecisionCompress = DecisionType.GetField("compress", any);
                DecisionGetBatches = ReflectionUtils.FindUniqueMethod(DecisionType, "GetBatches",
                    method => method.GetParameters().Length == 0);
                DecisionGetBatchCount = ReflectionUtils.FindUniqueMethod(DecisionType, "GetBatchCount",
                    method => method.GetParameters().Length == 0);
                DecisionGetFinalCost = ReflectionUtils.FindUniqueMethod(DecisionType, "GetFinalCost",
                    method => method.GetParameters().Length == 1);
                DecisionGetIndexBitCount = ReflectionUtils.FindUniqueMethod(DecisionType, "GetIndexBitCount",
                    method => !method.IsStatic && method.GetParameters().Length == 0);
                DecisionOptimize = ReflectionUtils.FindUniqueMethod(DecisionType, "Optimize",
                    method => method.GetParameters().Length == 1);
                if (DecisionGetBatches != null) {
                    var tupleType = DecisionGetBatches.ReturnType;
                    BatchesItem1 = tupleType.GetField("Item1");
                    BatchesItem2 = tupleType.GetField("Item2");
                }
            }

            SolverType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorSolverService");
            if (SolverType != null) {
                SolverPublicSolve = ReflectionUtils.FindUniqueMethod(SolverType, "GetParamsToOptimize",
                    method => method.GetParameters().Length == 0);
                SolverPrivateSolve = ReflectionUtils.FindUniqueMethod(SolverType, "GetParamsToOptimize",
                    method => method.GetParameters().Length == 5);
                SolverGetParamsUsedInMenu = ReflectionUtils.FindUniqueMethod(SolverType, "GetParamsUsedInMenu",
                    method => method.GetParameters().Length == 1);
                SolverParamsService = SolverType.GetField("paramsService", any);
                SolverControllers = SolverType.GetField("controllers", any);
                SolverExcService = SolverType.GetField("excService", any);
                SolverMenuService = SolverType.GetField("menuService", any);
            }

            SolverOutputType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorSolverOutput");
            if (SolverOutputType != null) {
                OutputDecision = SolverOutputType.GetField("decision", any);
                OutputOptions = SolverOutputType.GetField("options", any);
            }
            SelectionOptionsType = ReflectionUtils.FindType(
                "VF.Service.Compressor.ParameterCompressorSolverService+ParamSelectionOptions");
            OptionsAllowedMenuTypes = SelectionOptionsType?.GetField("allowedMenuTypes", any);

            CompressorServiceType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorService");
            CompressorApply = CompressorServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                CompressorServiceType, "Apply", method => method.GetParameters().Length == 0);
            ControllerManagerType = ReflectionUtils.FindType("VF.Utils.ControllerManager");
            LayerServiceType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorLayerService");
            if (LayerServiceType != null) {
                LayerBuildLayer = ReflectionUtils.FindUniqueMethod(LayerServiceType, "BuildLayer",
                    method => {
                        var parameters = method.GetParameters();
                        return parameters.Length == 2
                               && parameters[0].ParameterType == DecisionType
                               && parameters[1].ParameterType == ControllerManagerType;
                    });
            }

            var paramsServiceType = ReflectionUtils.FindType("VF.Service.ParamsService");
            ParamsGetReadOnly = paramsServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                paramsServiceType, "GetReadOnlyParams", method => method.GetParameters().Length == 0);
            var paramsExtType = ReflectionUtils.FindType("VF.Utils.VRCExpressionParametersExtensions");
            GetMaxCost = paramsExtType == null ? null : ReflectionUtils.FindUniqueMethod(
                paramsExtType, "GetMaxCost", method => method.GetParameters().Length == 0);
            // paramz.Clone() is the generic ObjectExtensions.Clone<T>(original, reason, prefix, recursive).
            var objectExtType = ReflectionUtils.FindType("VF.Utils.ObjectExtensions");
            var openClone = objectExtType?
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method => method.Name == "Clone"
                                           && method.IsGenericMethodDefinition
                                           && method.GetParameters().Length == 4);
            ParamsClone = openClone?.MakeGenericMethod(
                typeof(VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters));

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            if (controllersServiceType != null) {
                ControllersGetAll = ReflectionUtils.FindUniqueMethod(
                    controllersServiceType, "GetAllUsedControllers",
                    method => method.GetParameters().Length == 0);
                ControllersIsParamUsed = ReflectionUtils.FindMethodWithSignature(
                    controllersServiceType, "IsParamUsed", typeof(bool), typeof(string));
                ControllersParamsService = controllersServiceType.GetField("paramsService", any);
            }

            var menuServiceType = ReflectionUtils.FindType("VF.Service.MenuService");
            MenuGetReadOnly = menuServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                menuServiceType, "GetReadOnlyMenu", method => method.GetParameters().Length == 0);
            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            if (vfControllerType != null) {
                ControllerNewParam = ReflectionUtils.FindUniqueMethod(
                    vfControllerType, "_NewParam", method => method.GetParameters().Length == 3);
                ControllerParameters = vfControllerType.GetProperty("parameters", any);
                ControllerLayers = vfControllerType.GetProperty("layers", any);
            }
            var paramManagerType = ReflectionUtils.FindType("VF.Utils.ParamManager");
            ParamManagerAddSynced = paramManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                paramManagerType, "AddSyncedParam", method => method.GetParameters().Length == 1);
            var layerType = ReflectionUtils.FindType("VF.Utils.Controller.VFLayer");
            var getBehaviours = layerType == null ? null : ReflectionUtils.FindUniqueMethod(
                layerType,
                "GetBehaviours",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly,
                method => method.IsGenericMethodDefinition && method.GetParameters().Length == 0
            );
            LayerGetDrivers = getBehaviours?.MakeGenericMethod(
                typeof(VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver));

            var menuItemType = ReflectionUtils.FindType("VF.Menu.CompressorMenuItem");
            CompressorMenuItemGet = menuItemType == null ? null : ReflectionUtils.FindUniqueMethod(
                menuItemType, "Get", method => method.GetParameters().Length == 0);

            // SetAap(clip, name, FloatOrObjectCurve) — the curve arg converts from float via op_Implicit.
            var clipExtType = ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions");
            // Now an instance method on VFClip: SetAap(paramName, curve).
            var vfClipType = ReflectionUtils.FindType("VF.Utils.Controller.VFClip");
            ClipSetAap = vfClipType == null ? null : ReflectionUtils.FindUniqueMethod(vfClipType, "SetAap",
                method => method.GetParameters().Length == 2
                          && method.GetParameters()[0].ParameterType == typeof(string));
            var curveType = ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve");
            FloatToCurve = curveType?
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .SingleOrDefault(method => method.Name == "op_Implicit"
                                           && method.GetParameters().Length == 1
                                           && method.GetParameters()[0].ParameterType == typeof(float));
            MakeAap = ControllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                ControllerManagerType, "MakeAap", method => method.GetParameters().Length == 3);
            ControllerManagerOne = ControllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                ControllerManagerType, "One", method => method.GetParameters().Length == 0);
            var vfaApType = ReflectionUtils.FindType("VF.Utils.BlendtreeMath+VFAap");
            VfaApName = vfaApType == null ? null : ReflectionUtils.FindUniqueMethod(
                vfaApType, "Name", method => method.GetParameters().Length == 0);
            var factoryType = ReflectionUtils.FindType("VF.Utils.VrcfObjectFactory");
            FactoryCreate = factoryType == null ? null : ReflectionUtils.FindUniqueMethod(
                factoryType, "Create", method => !method.IsGenericMethodDefinition
                                                 && method.GetParameters().Length == 2
                                                 && method.GetParameters()[0].ParameterType == typeof(Type));
        }

        /** Members every compressor module needs; call from Install(). */
        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(DecisionType, "VF.Service.Compressor.OptimizationDecision");
            ReflectionUtils.Demand(DecisionNumberSlots, "OptimizationDecision.numberSlots");
            ReflectionUtils.Demand(DecisionBoolSlots, "OptimizationDecision.boolSlots");
            ReflectionUtils.Demand(DecisionUseBadPriority, "OptimizationDecision.useBadPriorityMethod");
            ReflectionUtils.Demand(DecisionCompress, "OptimizationDecision.compress");
            ReflectionUtils.Demand(DecisionGetBatches, "OptimizationDecision.GetBatches()");
            ReflectionUtils.Demand(DecisionGetBatchCount, "OptimizationDecision.GetBatchCount()");
            ReflectionUtils.Demand(DecisionGetFinalCost, "OptimizationDecision.GetFinalCost(int)");
            ReflectionUtils.Demand(DecisionOptimize, "OptimizationDecision.Optimize(int)");
            ReflectionUtils.Demand(BatchesItem1, "GetBatches return tuple Item1");
            ReflectionUtils.Demand(BatchesItem2, "GetBatches return tuple Item2");
            ReflectionUtils.Demand(CompressorApply, "ParameterCompressorService.Apply()");
        }

        internal static object NewSolverOutput() {
            return Activator.CreateInstance(SolverOutputType);
        }

        internal static object NewSelectionOptions() {
            return Activator.CreateInstance(SelectionOptionsType);
        }
    }
}
