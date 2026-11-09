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
        // VRCFury 1.1435.0+: parameters whose bit width or signedness the compressor can't keep.
        // Absent on older releases, which compress every eligible parameter.
        internal static MethodInfo HasStandardNetworkEncoding; // static (Parameter) → bool
        internal static FieldInfo SolverExpressionParams;     // ParameterCompressorSolverService.expressionParams
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
        internal static Type AvatarControllerType;            // VF.Utils.VFAvatarController
        internal static MethodInfo LayerBuildLayer;           // BuildLayer(OptimizationDecision, VFAvatarController)

        // VRCFury 1.1437.0 replaced ParamsService/ParamManager and the VRCExpressionParameters
        // extensions with ExpressionParamsService, which hands out detached entry snapshots.
        internal static MethodInfo ExpressionParamsSnapshot;  // ExpressionParamsService.GetSnapshot() → Parameter[]
        internal static MethodInfo ExpressionParamsTotalCost; // ExpressionParamsService.CalcTotalCost()
        internal static MethodInfo GetMaxCost;                // static ExpressionParamsService.GetMaxCost()

        // VRCFury 1.1372.0 deleted GetAllReadOnlyControllers (it re-loaded every controller from
        // its asset on each call). GetAllUsedControllers is the successor; it yields
        // VFAvatarController (a VFController), so the layer surface the solver needs is
        // reachable straight off the wrapper.
        internal static MethodInfo ControllersGetAll;         // ControllersService.GetAllUsedControllers()
        internal static MethodInfo MenuGetReadOnly;
        internal static PropertyInfo ControllerLayers;
        internal static MethodInfo LayerGetDrivers;
        internal static MethodInfo FactoryCreate;

        internal static MethodInfo CompressorMenuItemGet;     // CompressorMenuItem.Get()

        internal static MethodInfo ClipSetAap;                // VFClip.SetAap(string, FloatOrObjectCurve)
        internal static MethodInfo FloatToCurve;              // FloatOrObjectCurve.op_Implicit(float)
        internal static MethodInfo CreateUniqueFloat;         // VFAvatarController.CreateUniqueFloat(string, FloatOrBool)

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
                SolverExpressionParams = SolverType.GetField("expressionParams", any);
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
            AvatarControllerType = ReflectionUtils.FindType("VF.Utils.VFAvatarController");
            LayerServiceType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorLayerService");
            if (LayerServiceType != null && AvatarControllerType != null) {
                LayerBuildLayer = ReflectionUtils.FindUniqueMethod(LayerServiceType, "BuildLayer",
                    method => {
                        var parameters = method.GetParameters();
                        return parameters.Length == 2
                               && parameters[0].ParameterType == DecisionType
                               && parameters[1].ParameterType == AvatarControllerType;
                    });
            }

            HasStandardNetworkEncoding = ReflectionUtils.FindMethodWithSignature(
                ReflectionUtils.FindType("VF.Utils.VRCExpressionParameterExtensions"),
                "HasStandardNetworkEncoding", typeof(bool),
                typeof(VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter));

            var parameterArrayType = typeof(VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter[]);
            var expressionParamsType = ReflectionUtils.FindType("VF.Service.ExpressionParamsService");
            if (expressionParamsType != null) {
                ExpressionParamsSnapshot = ReflectionUtils.FindMethodWithSignature(
                    expressionParamsType, "GetSnapshot", parameterArrayType);
                ExpressionParamsTotalCost = ReflectionUtils.FindMethodWithSignature(
                    expressionParamsType, "CalcTotalCost", typeof(int));
                GetMaxCost = ReflectionUtils.FindUniqueMethod(expressionParamsType, "GetMaxCost",
                    method => method.IsStatic && method.GetParameters().Length == 0
                              && method.ReturnType == typeof(int));
            }
            if (SolverExpressionParams != null && SolverExpressionParams.FieldType != expressionParamsType) {
                SolverExpressionParams = null;
            }

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            if (controllersServiceType != null) {
                ControllersGetAll = ReflectionUtils.FindUniqueMethod(
                    controllersServiceType, "GetAllUsedControllers",
                    method => method.GetParameters().Length == 0);
            }

            var menuServiceType = ReflectionUtils.FindType("VF.Service.MenuService");
            MenuGetReadOnly = menuServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                menuServiceType, "GetReadOnlyMenu", method => method.GetParameters().Length == 0);
            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            if (vfControllerType != null) {
                ControllerLayers = vfControllerType.GetProperty("layers", any);
            }
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

            // An instance method on VFClip: SetAap(paramName, FloatOrObjectCurve); the curve arg
            // converts from float via op_Implicit.
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
            // MakeAap(name, def) became CreateUniqueFloat(name, def): the same VF<n>_ prefixed,
            // uniquely named float, now returned as a plain VFAParam.
            var floatOrBoolType = ReflectionUtils.FindType("VF.Utils.FloatOrBool");
            CreateUniqueFloat = AvatarControllerType == null || floatOrBoolType == null
                ? null
                : ReflectionUtils.FindMethodWithSignature(AvatarControllerType, "CreateUniqueFloat",
                    ReflectionUtils.FindType("VF.Utils.Controller.VFAParam"), typeof(string), floatOrBoolType);
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

        /** Stock's encoding check, or true on VRCFury releases without one. */
        internal static bool CanCompressEncoding(
            VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter parameter
        ) {
            return HasStandardNetworkEncoding == null
                   || (bool)ReflectionUtils.InvokeUnwrapped(HasStandardNetworkEncoding, null, new object[] { parameter });
        }

        internal static object NewSolverOutput() {
            return Activator.CreateInstance(SolverOutputType);
        }

        internal static object NewSelectionOptions() {
            return Activator.CreateInstance(SelectionOptionsType);
        }
    }
}
