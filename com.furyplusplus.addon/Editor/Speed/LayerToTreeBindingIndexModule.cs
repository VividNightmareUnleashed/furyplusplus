using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

namespace FuryPlusPlus {
    /**
     * Kills the O(L²·B) shared-binding scan in LayerToTreeService: OptimizeLayer enumerates
     * EVERY layer's binding set for EACH candidate layer. We prefix-replace Apply (~28 lines,
     * replicated exactly) and hand OptimizeLayer a dictionary whose enumerator yields only
     * the layers that share at least one binding with the current candidate (from an inverted
     * binding→layers index built once). Stock OptimizeLayer then re-applies all of its own
     * filters over the reduced stream — false positives are re-filtered, false negatives are
     * impossible (same binding instances and equality), and enumeration preserves insertion
     * order so the DoNotOptimizeException messages and debug report stay string-identical.
     */
    internal sealed class LayerToTreeBindingIndexModule : Module {
        internal static LayerToTreeBindingIndexModule Instance { get; private set; }

        internal LayerToTreeBindingIndexModule() {
            Instance = this;
        }

        internal override string Id => "layerToTreeBindingIndex";
        internal override string DisplayName => "Layer-to-tree binding index";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string Description =>
            "Replaces the quadratic shared-binding scan in VRCFury's layer-to-blendtree pass " +
            "with an inverted binding index.";

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            LayerToTreeBindingIndexPatch.Install(harmony, compat);
        }
    }

    /**
     * Generic on purpose: closed over VRCFury's internal VFLayer type at runtime via
     * MakeGenericType. LINQ's Enumerable.Where special-cases only arrays and List&lt;T&gt;,
     * so enumeration of a Dictionary subclass dispatches through IEnumerable&lt;T&gt; — which
     * this class re-implements to apply the per-candidate filter.
     */
    internal class FilteringDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IEnumerable<KeyValuePair<TKey, TValue>> {
        public readonly List<TKey> InsertionOrder = new List<TKey>();
        public Func<object, bool> UntypedFilter;

        public void AddOrdered(TKey key, TValue value) {
            this[key] = value;
            InsertionOrder.Add(key);
        }

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() {
            foreach (var key in InsertionOrder) {
                if (UntypedFilter != null && !UntypedFilter(key)) continue;
                yield return new KeyValuePair<TKey, TValue>(key, this[key]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return ((IEnumerable<KeyValuePair<TKey, TValue>>)this).GetEnumerator();
        }
    }

    internal static class LayerToTreeBindingIndexPatch {
        private static FieldInfo globalsField;
        private static FieldInfo directTreeServiceField;
        private static FieldInfo controllersField;
        private static FieldInfo allFeaturesField;
        private static MethodInfo getFx;
        private static MethodInfo getLayers;
        private static MethodInfo getManagedLayers;
        private static MethodInfo getBindingsAnimatedInLayer;
        private static MethodInfo optimizeLayer;
        private static MethodInfo layerRemove;
        private static MethodInfo dbtCreate;
        private static PropertyInfo layerNameProperty;
        private static Type directTreeOptimizerType;
        private static Type filteringDictType;
        private static MethodInfo addOrdered;
        private static FieldInfo untypedFilterField;
        private static MethodInfo makeLazy;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            var serviceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.LayerToTreeService"), "VF.Service.LayerToTreeService");
            var layerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFLayer"), "VF.Utils.Controller.VFLayer");
            var managerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.ControllerManager"), "VF.Utils.ControllerManager");
            var vfControllerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.Controller.VFController"), "VF.Utils.Controller.VFController");
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            var dbtServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.DbtLayerService"), "VF.Service.DbtLayerService");
            var blendTreeDirectType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VFBlendTreeDirect"), "VF.Utils.VFBlendTreeDirect");
            directTreeOptimizerType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.Feature.DirectTreeOptimizer"), "VF.Model.Feature.DirectTreeOptimizer");

            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            globalsField = ReflectionUtils.Demand(serviceType.GetField("globals", any), "LayerToTreeService.globals");
            directTreeServiceField = ReflectionUtils.Demand(
                serviceType.GetField("directTreeService", any), "LayerToTreeService.directTreeService");
            controllersField = ReflectionUtils.Demand(
                serviceType.GetField("controllers", any), "LayerToTreeService.controllers");
            allFeaturesField = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.GlobalsService")?.GetField("allFeaturesInRun", any),
                "GlobalsService.allFeaturesInRun");

            getFx = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetFx",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetFx()");
            // GetLayers is declared on the VFController base; FindUniqueMethod is DeclaredOnly.
            getLayers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(vfControllerType, "GetLayers",
                    method => method.GetParameters().Length == 0),
                "VFController.GetLayers()");
            getManagedLayers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(managerType, "GetManagedLayers",
                    method => method.GetParameters().Length == 0),
                "ControllerManager.GetManagedLayers()");
            getBindingsAnimatedInLayer = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(serviceType, "GetBindingsAnimatedInLayer",
                    method => method.GetParameters().Length == 1),
                "LayerToTreeService.GetBindingsAnimatedInLayer(VFLayer)");
            optimizeLayer = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(serviceType, "OptimizeLayer",
                    method => method.GetParameters().Length == 3),
                "LayerToTreeService.OptimizeLayer(...)");
            layerRemove = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(layerType, "Remove"), "VFLayer.Remove()");
            layerNameProperty = ReflectionUtils.Demand(
                layerType.GetProperty("name", any), "VFLayer.name");
            dbtCreate = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(dbtServiceType, "Create",
                    method => method.GetParameters().Length == 1
                              && method.GetParameters()[0].ParameterType == typeof(string)),
                "DbtLayerService.Create(string)");

            var apply = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(serviceType, "Apply"), "LayerToTreeService.Apply()");

            // The dictionary parameter of OptimizeLayer fixes the exact closed generic we must build.
            var dictParamType = optimizeLayer.GetParameters()[1].ParameterType;
            var dictArgs = dictParamType.GetGenericArguments(); // [VFLayer, ICollection<EditorCurveBinding>]
            filteringDictType = typeof(FilteringDictionary<,>).MakeGenericType(dictArgs[0], dictArgs[1]);
            if (!dictParamType.IsAssignableFrom(filteringDictType)) {
                throw new InvalidOperationException("target signature mismatch");
            }
            addOrdered = filteringDictType.GetMethod("AddOrdered");
            untypedFilterField = filteringDictType.GetField("UntypedFilter");
            makeLazy = typeof(LayerToTreeBindingIndexPatch)
                .GetMethod(nameof(MakeLazy), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(blendTreeDirectType);

            SelfTestEnumeratorDispatch();

            harmony.Patch(
                apply,
                // Priority.Low: the ported layer-index module's Begin prefix (default priority)
                // must run first so its GetLayerId/Exists context is live inside our body.
                prefix: new HarmonyMethod(
                    typeof(LayerToTreeBindingIndexPatch).GetMethod(
                        nameof(ApplyPrefix), BindingFlags.Static | BindingFlags.NonPublic)
                ) { priority = Priority.Low }
            );
        }

        /** Proves LINQ enumerates a FilteringDictionary through our re-implemented enumerator. */
        private static void SelfTestEnumeratorDispatch() {
            var probe = new FilteringDictionary<string, int>();
            probe.AddOrdered("a", 1);
            probe.AddOrdered("b", 2);
            probe.AddOrdered("c", 3);
            probe.UntypedFilter = key => (string)key != "b";
            var seen = probe.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
            if (seen.Length != 2 || seen[0] != "a" || seen[1] != "c") {
                throw new InvalidOperationException("filtering enumerator did not dispatch through LINQ");
            }
        }

        private static Lazy<T> MakeLazy<T>(Func<object> factory) {
            return new Lazy<T>(() => (T)factory());
        }

        private static bool ApplyPrefix(object __instance) {
            if (LayerToTreeBindingIndexModule.Instance?.Enabled != true) return true;

            object applyToLayers;
            object bindingsDict;
            Dictionary<object, HashSet<object>> relevantByLayer;
            object lazyTree;
            try {
                // ---- Phase 1: pure reads; any surprise → run stock Apply untouched. ----
                // Mirrors DisableDbtOptimizerMenuItem.Get() (SessionState-backed).
                if (SessionState.GetBool("com.vrcfury.disableDbt", false) && Application.isPlaying) {
                    return false; // stock Apply would return immediately too
                }

                var globals = globalsField.GetValue(__instance);
                var allFeatures = (IEnumerable)allFeaturesField.GetValue(globals);
                var applyToUnmanaged = allFeatures.Cast<object>()
                    .Any(feature => directTreeOptimizerType.IsInstanceOfType(feature));

                var fx = getFx.Invoke(controllersField.GetValue(__instance), null);
                applyToLayers = (applyToUnmanaged ? getLayers : getManagedLayers).Invoke(fx, null);
                var allLayers = ((IEnumerable)getLayers.Invoke(fx, null)).Cast<object>().ToList();

                bindingsDict = Activator.CreateInstance(filteringDictType);
                var invertedIndex = new Dictionary<EditorCurveBinding, List<object>>();
                var bindingsByLayerLocal = new Dictionary<object, ICollection<EditorCurveBinding>>();
                foreach (var layer in allLayers) {
                    var bindings = (ICollection<EditorCurveBinding>)ReflectionUtils.InvokeUnwrapped(
                        getBindingsAnimatedInLayer, __instance, new[] { layer });
                    addOrdered.Invoke(bindingsDict, new[] { layer, bindings });
                    bindingsByLayerLocal[layer] = bindings;
                    foreach (var binding in bindings) {
                        if (!invertedIndex.TryGetValue(binding, out var list)) {
                            list = new List<object>();
                            invertedIndex[binding] = list;
                        }
                        list.Add(layer);
                    }
                }

                relevantByLayer = new Dictionary<object, HashSet<object>>();
                foreach (var layer in allLayers) {
                    var relevant = new HashSet<object>();
                    foreach (var binding in bindingsByLayerLocal[layer]) {
                        foreach (var other in invertedIndex[binding]) relevant.Add(other);
                    }
                    relevantByLayer[layer] = relevant;
                }

                var directTreeService = directTreeServiceField.GetValue(__instance);
                lazyTree = makeLazy.Invoke(null, new object[] {
                    (Func<object>)(() => ReflectionUtils.InvokeUnwrapped(
                        dbtCreate, directTreeService, new object[] { null }))
                });
            } catch (Exception e) {
                Log.Warn("Layer-to-tree binding index fell back to VRCFury: " + e.Message);
                return true;
            }

            // ---- Phase 2: mutation loop, semantics identical to stock Apply. Unexpected
            // exceptions propagate exactly like stock (they would fail the build there too).
            var debugLog = new List<string>();
            foreach (var layer in ((IEnumerable)applyToLayers).Cast<object>()) {
                relevantByLayer.TryGetValue(layer, out var relevant);
                untypedFilterField.SetValue(bindingsDict,
                    relevant == null ? (Func<object, bool>)null : relevant.Contains);
                var layerName = layerNameProperty.GetValue(layer);
                try {
                    optimizeLayer.Invoke(__instance, new[] { layer, bindingsDict, lazyTree });
                    debugLog.Add($"{layerName} - OPTIMIZED");
                    ReflectionUtils.InvokeUnwrapped(layerRemove, layer, null);
                } catch (TargetInvocationException wrapped)
                    when (wrapped.InnerException?.GetType().Name == "DoNotOptimizeException") {
                    debugLog.Add($"{layerName} - Not Optimizing ({wrapped.InnerException.Message})");
                } catch (TargetInvocationException wrapped) when (wrapped.InnerException != null) {
                    ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
                }
            }
            Debug.Log("Optimization report:\n\n" + string.Join("\n", debugLog));
            return false;
        }
    }
}
