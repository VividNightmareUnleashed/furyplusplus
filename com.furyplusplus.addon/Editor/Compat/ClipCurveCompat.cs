using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Lazy area holder for VRCFury's clip/curve surface shared by the clip-facing quality
     * and speed modules. A rename in VRCFury is then fixed here instead of in every pass.
     * Members stay null on resolution failure; consuming modules Demand what they need from
     * their own Install (fail-closed). The curve-tuple FieldInfos are cached here so
     * per-curve loops never touch the member tables.
     *
     * As of VRCFury 1.1382 clips are detached in-memory VFClip objects rather than
     * AnimationClip assets, and their curve keys are VFBinding (a resolved-object handle)
     * rather than EditorCurveBinding. VFBinding is a struct, so bindings travel through
     * here boxed as object — safe as dictionary/set keys because it overrides Equals and
     * GetHashCode.
     */
    internal static class ClipCurveCompat {
        private static bool resolved;

        // ---- controllers ----
        internal static MethodInfo GetAllUsedControllers;   // ControllersService.GetAllUsedControllers()
        internal static MethodInfo ControllerGetClips;      // VFController.GetClips(IEnumerable<VFLayer>)
        internal static MethodInfo ControllerGetLayers;     // VFController.GetLayers()

        // ---- VFClip (all instance members now) ----
        internal static Type ClipType;                      // VF.Utils.Controller.VFClip
        internal static MethodInfo ClipGetAllCurves;        // VFClip.GetAllCurves()
        internal static MethodInfo ClipGetAllBindings;      // VFClip.GetAllBindings()
        internal static MethodInfo ClipSetCurves;           // VFClip.SetCurves(IEnumerable<(VFBinding, curve)>)
        internal static MethodInfo ClipIsProxyClip;         // VFClip.IsProxyClip()
        internal static MethodInfo ClipGetUseOriginalUserClip; // VFClip.GetUseOriginalUserClip(VFGameObject)
        internal static MethodInfo ClipGetSourceAsset;      // VFMotion.GetSourceAsset()
        internal static PropertyInfo ClipName;              // VFClip.name
        internal static MethodInfo ClipGetLengthInSeconds;  // VFClip.GetLengthInSeconds()
        internal static MethodInfo ClipIsLooping;           // VFClip.IsLooping()
        internal static MethodInfo ClipGetAdditiveRefPose;  // VFClip.GetAdditiveReferencePoseClip()
        internal static FieldInfo ClipFrameRate;            // VFClip.frameRate

        // ---- curve tuple: (VFBinding, FloatOrObjectCurve) ----
        internal static Type CurveTupleType;
        internal static FieldInfo CurveTupleItem1;
        internal static FieldInfo CurveTupleItem2;
        internal static PropertyInfo CurveIsFloat;          // FloatOrObjectCurve.IsFloat
        internal static PropertyInfo CurveFloatCurve;       // FloatOrObjectCurve.FloatCurve
        internal static PropertyInfo CurveObjectCurve;      // FloatOrObjectCurve.ObjectCurve

        // ---- VFBinding ----
        internal static Type BindingType;                   // VF.Utils.VFBinding
        internal static PropertyInfo BindingPropertyName;
        internal static PropertyInfo BindingClrType;        // VFBinding.type
        internal static PropertyInfo BindingTarget;         // VFBinding.target → VFGameObject
        internal static MethodInfo BindingIsAnimatorBinding;
        internal static MethodInfo BindingTryGetCurrentFloat; // (VFGameObject, out float)
        internal static MethodInfo BindingNormalize;        // (bool combineRotation)
        internal static MethodInfo BindingGetDebugPath;     // (VFGameObject root)
        internal static MethodInfo BindingToEditorCurveBinding; // (VFGameObject root)

        // ---- VFGameObject ----
        internal static MethodInfo WrapGameObjectOp;        // op_Implicit(GameObject) -> VFGameObject

        // A List<(VFBinding, FloatOrObjectCurve)> ctor, for handing SetCurves a typed batch.
        private static Type curveListType;

        internal static void EnsureResolved() {
            if (resolved) return;
            resolved = true;

            var controllersServiceType = ReflectionUtils.FindType("VF.Service.ControllersService");
            GetAllUsedControllers = controllersServiceType == null ? null : ReflectionUtils.FindUniqueMethod(
                controllersServiceType, "GetAllUsedControllers",
                method => method.GetParameters().Length == 0);

            var vfControllerType = ReflectionUtils.FindType("VF.Utils.Controller.VFController");
            if (vfControllerType != null) {
                ControllerGetClips = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetClips",
                    method => method.GetParameters().Length == 1);
                ControllerGetLayers = ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                    method => method.GetParameters().Length == 0);
            }

            ClipType = ReflectionUtils.FindType("VF.Utils.Controller.VFClip");
            if (ClipType != null) {
                ClipGetAllCurves = ReflectionUtils.FindUniqueMethod(ClipType, "GetAllCurves",
                    method => method.GetParameters().Length == 0);
                ClipGetAllBindings = ReflectionUtils.FindUniqueMethod(ClipType, "GetAllBindings",
                    method => method.GetParameters().Length == 0);
                ClipSetCurves = ReflectionUtils.FindUniqueMethod(ClipType, "SetCurves",
                    method => method.GetParameters().Length == 1);
                ClipIsProxyClip = ReflectionUtils.FindUniqueMethod(ClipType, "IsProxyClip",
                    method => method.GetParameters().Length == 0);
                ClipGetUseOriginalUserClip = ReflectionUtils.FindUniqueMethod(ClipType, "GetUseOriginalUserClip",
                    method => method.GetParameters().Length == 1);
                ClipGetSourceAsset = ReflectionUtils.FindUniqueMethod(ClipType, "GetSourceAsset",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    method => method.GetParameters().Length == 0);
                ClipName = ClipType.GetProperty("name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ClipGetLengthInSeconds = ReflectionUtils.FindUniqueMethod(ClipType, "GetLengthInSeconds",
                    method => method.GetParameters().Length == 0);
                ClipIsLooping = ReflectionUtils.FindUniqueMethod(ClipType, "IsLooping",
                    method => method.GetParameters().Length == 0);
                ClipGetAdditiveRefPose = ReflectionUtils.FindUniqueMethod(ClipType,
                    "GetAdditiveReferencePoseClip", method => method.GetParameters().Length == 0);
                ClipFrameRate = ClipType.GetField("frameRate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            CurveTupleType = ClipGetAllCurves?.ReturnType.GetElementType();
            CurveTupleItem1 = CurveTupleType?.GetField("Item1");
            CurveTupleItem2 = CurveTupleType?.GetField("Item2");
            curveListType = CurveTupleType == null ? null : typeof(List<>).MakeGenericType(CurveTupleType);

            var curveType = ReflectionUtils.FindType("VF.Utils.FloatOrObjectCurve");
            CurveIsFloat = curveType?.GetProperty("IsFloat");
            CurveFloatCurve = curveType?.GetProperty("FloatCurve");
            CurveObjectCurve = curveType?.GetProperty("ObjectCurve");

            BindingType = ReflectionUtils.FindType("VF.Utils.VFBinding");
            if (BindingType != null) {
                const BindingFlags instance =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                BindingPropertyName = BindingType.GetProperty("propertyName", instance);
                BindingClrType = BindingType.GetProperty("type", instance);
                BindingTarget = BindingType.GetProperty("target", instance);
                BindingIsAnimatorBinding = ReflectionUtils.FindUniqueMethod(BindingType, "IsAnimatorBinding",
                    method => !method.IsStatic && method.GetParameters().Length == 0);
                BindingTryGetCurrentFloat = ReflectionUtils.FindUniqueMethod(BindingType, "TryGetCurrentFloat",
                    method => method.GetParameters().Length == 2);
                BindingNormalize = ReflectionUtils.FindUniqueMethod(BindingType, "Normalize",
                    method => method.GetParameters().Length == 1);
                BindingGetDebugPath = ReflectionUtils.FindUniqueMethod(BindingType, "GetDebugPath",
                    method => method.GetParameters().Length == 1);
                BindingToEditorCurveBinding = ReflectionUtils.FindUniqueMethod(
                    BindingType, "ToEditorCurveBinding",
                    method => method.GetParameters().Length == 1
                              && method.ReturnType == typeof(UnityEditor.EditorCurveBinding));
            }

            VfGameObjectCompat.EnsureResolved();
            var vfGameObjectType = VfGameObjectCompat.VfGameObjectType;
            // The VFGameObject constructor is private; the public implicit conversion from
            // GameObject is the only supported way in.
            WrapGameObjectOp = vfGameObjectType == null ? null : ReflectionUtils.FindUniqueMethod(
                vfGameObjectType, "op_Implicit",
                method => method.IsStatic
                          && method.ReturnType == vfGameObjectType
                          && method.GetParameters().Length == 1
                          && method.GetParameters()[0].ParameterType == typeof(GameObject));
        }

        /** The surface every clip-walking consumer shares; call from Install(). */
        internal static void DemandCore() {
            EnsureResolved();
            ReflectionUtils.Demand(GetAllUsedControllers, "ControllersService.GetAllUsedControllers()");
            ReflectionUtils.Demand(ControllerGetClips, "VFController.GetClips(layers)");
            ReflectionUtils.Demand(ClipType, "VF.Utils.Controller.VFClip");
            ReflectionUtils.Demand(ClipGetAllCurves, "VFClip.GetAllCurves()");
            ReflectionUtils.Demand(ClipIsProxyClip, "VFClip.IsProxyClip()");
            ReflectionUtils.Demand(CurveTupleType, "(VFBinding, FloatOrObjectCurve)");
            ReflectionUtils.Demand(CurveTupleItem1, "curve tuple Item1");
            ReflectionUtils.Demand(CurveTupleItem2, "curve tuple Item2");
            ReflectionUtils.Demand(CurveIsFloat, "FloatOrObjectCurve.IsFloat");
            ReflectionUtils.Demand(CurveFloatCurve, "FloatOrObjectCurve.FloatCurve");
            ReflectionUtils.Demand(BindingType, "VF.Utils.VFBinding");
            ReflectionUtils.Demand(BindingPropertyName, "VFBinding.propertyName");
            ReflectionUtils.Demand(BindingIsAnimatorBinding, "VFBinding.IsAnimatorBinding()");
            ReflectionUtils.Demand(WrapGameObjectOp, "VFGameObject op_Implicit(GameObject)");
        }

        // ---- typed accessors (hot per-curve loops; no member-table lookups) ----

        /** Boxed VF.Utils.VFBinding. Safe as a dictionary/set key: Equals+GetHashCode are overridden. */
        internal static object TupleBinding(object entry) {
            return CurveTupleItem1.GetValue(entry);
        }

        internal static object TupleCurve(object entry) {
            return CurveTupleItem2.GetValue(entry);
        }

        internal static bool IsFloat(object curve) {
            return (bool)CurveIsFloat.GetValue(curve);
        }

        internal static AnimationCurve FloatCurveOf(object curve) {
            return CurveFloatCurve.GetValue(curve) as AnimationCurve;
        }

        internal static UnityEditor.ObjectReferenceKeyframe[] ObjectCurveOf(object curve) {
            return CurveObjectCurve.GetValue(curve) as UnityEditor.ObjectReferenceKeyframe[];
        }

        // ---- VFBinding ----

        internal static string PropertyNameOf(object binding) {
            return BindingPropertyName.GetValue(binding) as string;
        }

        internal static Type ClrTypeOf(object binding) {
            return BindingClrType?.GetValue(binding) as Type;
        }

        /**
         * The scene object this binding resolves to, as a boxed VFGameObject — null when the
         * binding is unresolved. Usable as a dictionary key (VFGameObject overrides Equals and
         * GetHashCode), which is what lets consumers bucket bindings by target instead of
         * rescanning the whole binding list per object.
         */
        internal static object TargetOf(object binding) {
            return BindingTarget?.GetValue(binding);
        }

        /**
         * True for animator-stream bindings (AAPs and humanoid muscles) — parameters rather
         * than scene properties. VRCFury's own test, which correctly excludes an Animator's
         * m_Enabled (that one really is a scene property).
         */
        internal static bool IsAnimatorBinding(object binding) {
            return (bool)BindingIsAnimatorBinding.Invoke(binding, null);
        }

        /**
         * The avatar's current (resting) value for this binding. Delegates to VRCFury, which
         * suppresses material property drawers around the lookup — going through
         * AnimationUtility directly here would be markedly slower on material bindings.
         */
        internal static bool TryGetRestValue(object binding, object vfAvatarRoot, out float value) {
            var args = new[] { vfAvatarRoot, null };
            var found = (bool)BindingTryGetCurrentFloat.Invoke(binding, args);
            value = found && args[1] is float f ? f : 0f;
            return found;
        }

        /** Matches the normalization VRCFury applies to the bindings it indexes per layer. */
        internal static object Normalize(object binding, bool combineRotation) {
            return BindingNormalize.Invoke(binding, new object[] { combineRotation });
        }

        internal static string DebugPathOf(object binding) {
            if (BindingGetDebugPath == null) return "";
            try {
                return BindingGetDebugPath.Invoke(binding, new object[] { null }) as string ?? "";
            } catch {
                return "";
            }
        }

        /**
         * The binding as it will actually be written into the saved AnimationClip — the form
         * ClipContentKey serializes, so content keys stay comparable with what lands on disk.
         */
        internal static UnityEditor.EditorCurveBinding ToEditorCurveBinding(object binding, object vfRoot) {
            return (UnityEditor.EditorCurveBinding)BindingToEditorCurveBinding
                .Invoke(binding, new[] { vfRoot });
        }

        internal static object WrapGameObject(GameObject gameObject) {
            if (gameObject == null || WrapGameObjectOp == null) return null;
            return WrapGameObjectOp.Invoke(null, new object[] { gameObject });
        }

        // ---- VFClip ----

        internal static Array AllCurvesOf(object clip) {
            return (Array)ClipGetAllCurves.Invoke(clip, null);
        }

        internal static Array AllBindingsOf(object clip) {
            return (Array)ClipGetAllBindings.Invoke(clip, null);
        }

        internal static bool IsProxyClip(object clip) {
            return (bool)ClipIsProxyClip.Invoke(clip, null);
        }

        internal static object GetUseOriginalUserClip(object clip, object vfBindingRoot) {
            return ClipGetUseOriginalUserClip.Invoke(clip, new[] { vfBindingRoot });
        }

        /**
         * Builds the typed List<(VFBinding, FloatOrObjectCurve)> SetCurves wants. A null
         * curve removes that binding, which is how both strip passes delete curves.
         */
        internal static object NewCurveBatch() {
            return Activator.CreateInstance(curveListType);
        }

        internal static void AddToBatch(object batch, object binding, object curve) {
            var tuple = Activator.CreateInstance(CurveTupleType, binding, curve);
            ((IList)batch).Add(tuple);
        }

        internal static void SetCurves(object clip, object batch) {
            ClipSetCurves.Invoke(clip, new[] { batch });
        }

        /** Every clip reachable from one VFController/ControllerManager wrapper. */
        internal static IEnumerable ClipsFrom(object vfController) {
            return (IEnumerable)ControllerGetClips.Invoke(vfController, new object[] { null });
        }

        /** Every used controller of the build, as VFController/ControllerManager wrappers. */
        internal static IEnumerable<object> UsedControllers(object controllersService) {
            return ((IEnumerable)GetAllUsedControllers.Invoke(controllersService, null)).Cast<object>();
        }
    }
}
