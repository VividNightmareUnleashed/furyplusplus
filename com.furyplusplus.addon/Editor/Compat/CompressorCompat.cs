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

        internal static Type SolverOutputType;                // ParameterCompressorSolverOutput
        internal static FieldInfo OutputDecision;
        internal static FieldInfo OutputOptions;
        internal static Type SelectionOptionsType;            // ParamSelectionOptions
        internal static FieldInfo OptionsAllowedMenuTypes;

        internal static Type CompressorServiceType;           // ParameterCompressorService
        internal static MethodInfo CompressorApply;           // Apply()
        internal static Type LayerServiceType;                // ParameterCompressorLayerService
        internal static MethodInfo LayerBuildLayer;           // BuildLayer(OptimizationDecision)
        internal static FieldInfo LayerServiceControllers;    // ControllersService field

        internal static MethodInfo ParamsGetReadOnly;         // ParamsService.GetReadOnlyParams()
        internal static MethodInfo GetMaxCost;                // VRCExpressionParametersExtensions.GetMaxCost()
        internal static MethodInfo ParamsClone;               // VRCExpressionParametersExtensions.Clone(paramz)

        internal static MethodInfo ControllersGetAllReadOnly; // ControllersService.GetAllReadOnlyControllers()

        internal static MethodInfo CompressorMenuItemGet;     // CompressorMenuItem.Get()

        internal static MethodInfo ClipSetAap;                // AnimationClipExtensions.SetAap(clip, string, FloatOrObjectCurve)
        internal static MethodInfo FloatToCurve;              // FloatOrObjectCurve.op_Implicit(float)
        internal static MethodInfo MakeAap;                   // ControllerManager.MakeAap(string, float, bool)
        internal static MethodInfo GetFx;                     // ControllersService.GetFx()

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
            LayerServiceType = ReflectionUtils.FindType("VF.Service.Compressor.ParameterCompressorLayerService");
            if (LayerServiceType != null) {
                LayerBuildLayer = ReflectionUtils.FindUniqueMethod(LayerServiceType, "BuildLayer",
                    method => method.GetParameters().Length == 1);
                LayerServiceControllers = LayerServiceType.GetField("controllers", any);
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
                ControllersGetAllReadOnly = ReflectionUtils.FindUniqueMethod(
                    controllersServiceType, "GetAllReadOnlyControllers",
                    method => method.GetParameters().Length == 0);
                GetFx = ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetFx",
                    method => method.GetParameters().Length == 0);
            }

            var menuItemType = ReflectionUtils.FindType("VF.Menu.CompressorMenuItem");
            CompressorMenuItemGet = menuItemType == null ? null : ReflectionUtils.FindUniqueMethod(
                menuItemType, "Get", method => method.GetParameters().Length == 0);

            // SetAap(clip, name, FloatOrObjectCurve) — the curve arg converts from float via op_Implicit.
            var clipExtType = ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions");
            ClipSetAap = clipExtType == null ? null : ReflectionUtils.FindUniqueMethod(clipExtType, "SetAap",
                method => method.GetParameters().Length == 3);
            var curveType = ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve");
            FloatToCurve = curveType?
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .SingleOrDefault(method => method.Name == "op_Implicit"
                                           && method.GetParameters().Length == 1
                                           && method.GetParameters()[0].ParameterType == typeof(float));
            var controllerManagerType = ReflectionUtils.FindType("VF.Utils.ControllerManager");
            MakeAap = controllerManagerType == null ? null : ReflectionUtils.FindUniqueMethod(
                controllerManagerType, "MakeAap", method => method.GetParameters().Length == 3);
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
    }
}
