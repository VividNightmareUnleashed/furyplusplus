using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for VRCFury's clip/curve surface (AnimationClipExtensions,
     * FloatOrObjectCurve, the AnimatorIterator clip walker, the VFController unwrap)
     * shared by the clip-facing quality and speed modules. A rename in VRCFury is then
     * fixed here instead of in every pass. Members stay null on resolution failure;
     * consuming modules Demand what they need from their own Install (fail-closed).
     * The curve-tuple FieldInfos are cached here so per-curve loops never touch the
     * member tables.
     */
    internal static class ClipCurveCompat {
        private static bool resolved;

        internal static MethodInfo GetAllUsedControllers;  // ControllersService.GetAllUsedControllers()
        internal static FieldInfo ControllerCtrlField;     // VFController.ctrl → AnimatorController
        internal static Type ClipsIteratorType;            // VF.Utils.AnimatorIterator+Clips
        internal static MethodInfo ClipsFromController;    // Clips.From(VFController)
        internal static MethodInfo GetAllCurves;           // AnimationClipExtensions.GetAllCurves(clip)
        internal static MethodInfo GetAllBindings;         // AnimationClipExtensions.GetAllBindings(clip)
        internal static MethodInfo SetCurves;              // AnimationClipExtensions.SetCurves(clip, curves)
        internal static MethodInfo IsProxyClipMethod;      // AnimationClipExtensions.IsProxyClip(clip)
        internal static MethodInfo GetUseOriginalUserClip; // AnimationClipExtensions.GetUseOriginalUserClip(clip)
        internal static Type CurveTupleType;               // (EditorCurveBinding, FloatOrObjectCurve)
        internal static FieldInfo CurveTupleItem1;
        internal static FieldInfo CurveTupleItem2;
        internal static PropertyInfo CurveIsFloat;         // FloatOrObjectCurve.IsFloat
        internal static PropertyInfo CurveFloatCurve;      // FloatOrObjectCurve.FloatCurve
        internal static PropertyInfo CurveObjectCurve;     // FloatOrObjectCurve.ObjectCurve

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            GetAllUsedControllers = controllersServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                controllersServiceType, "GetAllUsedControllers",
                method => method.GetParameters().Length == 0);

            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            ControllerCtrlField = vfControllerType?.GetField("ctrl",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            ClipsIteratorType = ReflectionUtils.FindType("VF.Utils.AnimatorIterator+Clips");
            ClipsFromController = ClipsIteratorType == null || vfControllerType == null
                ? null
                : ClipsIteratorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(method => method.Name == "From"
                                               && method.GetParameters().Length == 1
                                               && method.GetParameters()[0].ParameterType == vfControllerType);

            var clipExtType = ReflectionUtils.FindType("VF.Utils.AnimationClipExtensions");
            if (clipExtType != null) {
                GetAllCurves = ReflectionUtils.FindUniqueMethod(clipExtType, "GetAllCurves",
                    method => method.GetParameters().Length == 1);
                GetAllBindings = ReflectionUtils.FindUniqueMethod(clipExtType, "GetAllBindings",
                    method => method.GetParameters().Length == 1);
                SetCurves = ReflectionUtils.FindUniqueMethod(clipExtType, "SetCurves",
                    method => method.GetParameters().Length == 2);
                IsProxyClipMethod = ReflectionUtils.FindUniqueMethod(clipExtType, "IsProxyClip",
                    method => method.GetParameters().Length == 1);
                GetUseOriginalUserClip = ReflectionUtils.FindUniqueMethod(clipExtType, "GetUseOriginalUserClip",
                    method => method.GetParameters().Length == 1);
            }

            CurveTupleType = GetAllCurves?.ReturnType.GetElementType();
            CurveTupleItem1 = CurveTupleType?.GetField("Item1");
            CurveTupleItem2 = CurveTupleType?.GetField("Item2");

            var curveType = ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve");
            CurveIsFloat = curveType?.GetProperty("IsFloat");
            CurveFloatCurve = curveType?.GetProperty("FloatCurve");
            CurveObjectCurve = curveType?.GetProperty("ObjectCurve");
        }

        /** The surface every clip-walking consumer shares; call from Install(). */
        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(GetAllUsedControllers, "ControllersService.GetAllUsedControllers()");
            ReflectionUtils.Demand(GetAllCurves, "AnimationClipExtensions.GetAllCurves(clip)");
            ReflectionUtils.Demand(IsProxyClipMethod, "AnimationClipExtensions.IsProxyClip(clip)");
            ReflectionUtils.Demand(CurveTupleType, "(EditorCurveBinding, FloatOrObjectCurve)");
            ReflectionUtils.Demand(CurveTupleItem1, "curve tuple Item1");
            ReflectionUtils.Demand(CurveTupleItem2, "curve tuple Item2");
            ReflectionUtils.Demand(CurveIsFloat, "FloatOrObjectCurve.IsFloat");
            ReflectionUtils.Demand(CurveFloatCurve, "FloatOrObjectCurve.FloatCurve");
        }

        // ---- typed accessors (hot per-curve loops; no member-table lookups) ----

        internal static EditorCurveBinding TupleBinding(object entry) {
            return (EditorCurveBinding)CurveTupleItem1.GetValue(entry);
        }

        internal static object TupleCurve(object entry) {
            return CurveTupleItem2.GetValue(entry);
        }

        internal static object CreateTuple(EditorCurveBinding binding, object curve) {
            return Activator.CreateInstance(CurveTupleType, binding, curve);
        }

        internal static bool IsFloat(object curve) {
            return (bool)CurveIsFloat.GetValue(curve);
        }

        internal static AnimationCurve FloatCurveOf(object curve) {
            return CurveFloatCurve.GetValue(curve) as AnimationCurve;
        }

        internal static ObjectReferenceKeyframe[] ObjectCurveOf(object curve) {
            return CurveObjectCurve.GetValue(curve) as ObjectReferenceKeyframe[];
        }

        internal static bool IsProxyClip(AnimationClip clip) {
            return (bool)IsProxyClipMethod.Invoke(null, new object[] { clip });
        }

        internal static Array AllCurvesOf(AnimationClip clip) {
            return (Array)GetAllCurves.Invoke(null, new object[] { clip });
        }

        /** Every clip reachable from one VFController/ControllerManager wrapper. */
        internal static IEnumerable ClipsFrom(object vfController) {
            var iterator = Activator.CreateInstance(ClipsIteratorType);
            return (IEnumerable)ClipsFromController.Invoke(iterator, new[] { vfController });
        }

        /** The raw AnimatorController behind a VFController/ControllerManager wrapper. */
        internal static UnityEditor.Animations.AnimatorController RawController(object vfController) {
            return ControllerCtrlField.GetValue(vfController) as UnityEditor.Animations.AnimatorController;
        }
    }
}
