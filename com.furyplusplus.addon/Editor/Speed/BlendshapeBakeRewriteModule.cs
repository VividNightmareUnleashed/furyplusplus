using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace FuryPlusPlus {
    /**
     * Output-identical rewrite of BlendshapeOptimizerBuilder.Apply fixing its three
     * compounding bake-time problems (prerequisite for auto-enabling the optimizer):
     *  (a) the animated-bindings collection was rebuilt from EVERY controller once PER
     *      skinned mesh — it is skin-invariant and is now hoisted to one pass;
     *  (b) SavedBlendshape extracted three full vertex arrays per frame for EVERY shape
     *      (kept or baked) before ClearBlendShapes — GetMutableMesh clones the mesh, so
     *      the ORIGINAL keeps all data and kept shapes stream through shared buffers;
     *  (c) BakeTo round-tripped mesh.vertices/normals/tangents per baked shape — deltas
     *      now accumulate in arrays read once and written once (same float32 op sequence
     *      in the same shape order → bit-identical).
     * The upstream multi-frame interpolation bug (`weight100 <= frames.Count` — compares
     * against the frame COUNT) is FIXED by default per project policy, with a sub-toggle
     * to restore stock-identical behavior; log strings match stock byte-for-byte.
     */
    internal sealed class BlendshapeBakeRewriteModule : Module<BlendshapeBakeRewriteModule> {
        internal static readonly ModuleOption FixMultiFrameInterpolation = new ModuleOption(
            "fixMultiFrameInterpolation", "Fix multi-frame blendshape interpolation", true,
            "VRCFury selects the interpolation frame by comparing the weight against the frame " +
            "COUNT (and errors when the weight exceeds it). This selects by frame weight as " +
            "intended. Disable for stock-identical (buggy) behavior.",
            affectsBakeOutput: true);

        private static readonly ModuleOption[] AllOptions = {
            FixMultiFrameInterpolation
        };

        internal override string Id => "blendshapeBakeRewrite";
        internal override string DisplayName => "Fast blendshape optimizer bake";
        internal override ModuleKind Kind => ModuleKind.Speed;
        internal override string SettingsGroup => "Controllers & animation";
        internal override string Description =>
            "Rewrites VRCFury's Blendshape Optimizer bake to avoid gigabytes of array " +
            "churn on dense meshes. Output is bit-identical except the (default-on) " +
            "multi-frame interpolation fix below.";

        internal override System.Collections.Generic.IReadOnlyList<ModuleOption> Options => AllOptions;

        internal override void Install(Harmony harmony, VrcfuryCompat compat) {
            BlendshapeBakeRewritePatch.Install(harmony, compat);
        }
    }

    internal static class BlendshapeBakeRewritePatch {
        private static Type mmdCompatibilityType;
        private static FieldInfo globalsField;
        private static FieldInfo allFeaturesField;
        private static FieldInfo avatarObjectField;
        private static FieldInfo avatarField;
        private static FieldInfo controllersField;
        private static FieldInfo animatorsField;
        private static MethodInfo getBindings;
        private static MethodInfo getAllUsedControllers;
        private static MethodInfo getSubControllers;
        private static MethodInfo vfGetComponentsInSelfAndChildren;
        private static MethodInfo skinGetMesh;
        private static MethodInfo skinGetMutableMesh;
        private static MethodInfo skinOwner;
        private static MethodInfo ownerGetPath;
        private static MethodInfo isMaybeMmdBlendshape;
        private static MethodInfo meshDirty;
        // Tuple element fields of GetBindings/GetSubControllers entries, cached from the
        // first entry seen (the runtime types are stable per domain load) so per-entry
        // loops never touch the member tables.
        private static FieldInfo bindingsItem1;
        private static FieldInfo bindingsItem2;
        private static FieldInfo pairItem1;
        private static FieldInfo pairItem2;

        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var builderType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Feature.BlendshapeOptimizerBuilder"),
                "VF.Feature.BlendshapeOptimizerBuilder");
            mmdCompatibilityType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Model.Feature.MmdCompatibility"), "VF.Model.Feature.MmdCompatibility");
            var globalsType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.GlobalsService"), "VF.Service.GlobalsService");
            var controllersServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.ControllersService"), "VF.Service.ControllersService");
            var animatorsServiceType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Service.AnimatorHolderService"), "VF.Service.AnimatorHolderService");
            var vfGameObjectType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Utils.VFGameObject"), "VF.Utils.VFGameObject");
            var mmdUtilsType = ReflectionUtils.Demand(
                ReflectionUtils.FindType("VF.Builder.MmdUtils"), "VF.Builder.MmdUtils");

            globalsField = ReflectionUtils.Demand(builderType.GetField("globals", any), "builder.globals");
            allFeaturesField = ReflectionUtils.Demand(
                globalsType.GetField("allFeaturesInRun", any), "GlobalsService.allFeaturesInRun");
            avatarObjectField = ReflectionUtils.Demand(
                builderType.GetField("avatarObject", any), "builder.avatarObject");
            avatarField = ReflectionUtils.Demand(builderType.GetField("avatar", any), "builder.avatar");
            controllersField = ReflectionUtils.Demand(
                builderType.GetField("controllers", any), "builder.controllers");
            animatorsField = ReflectionUtils.Demand(builderType.GetField("animators", any), "builder.animators");
            getBindings = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(builderType, "GetBindings",
                    method => method.GetParameters().Length == 2),
                "builder.GetBindings(owner, controller)");
            getAllUsedControllers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(controllersServiceType, "GetAllUsedControllers",
                    method => method.GetParameters().Length == 0),
                "ControllersService.GetAllUsedControllers()");
            getSubControllers = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(animatorsServiceType, "GetSubControllers",
                    method => method.GetParameters().Length == 0),
                "AnimatorHolderService.GetSubControllers()");
            vfGetComponentsInSelfAndChildren = ReflectionUtils.Demand(
                vfGameObjectType.GetMethods(any)
                    .SingleOrDefault(method => method.Name == "GetComponentsInSelfAndChildren"
                                               && method.IsGenericMethodDefinition
                                               && method.GetParameters().Length == 0),
                "VFGameObject.GetComponentsInSelfAndChildren<T>()")
                .MakeGenericMethod(typeof(SkinnedMeshRenderer));
            VfGameObjectCompat.DemandCore();

            // Extension methods used by the stock body.
            skinGetMesh = ReflectionUtils.Demand(
                FindExtension("VF.Utils.RendererExtensions", "GetMesh", 1)
                ?? FindExtension("VF.Utils.SkinnedMeshRendererExtensions", "GetMesh", 1),
                "skin.GetMesh()");
            skinGetMutableMesh = ReflectionUtils.Demand(
                FindExtension("VF.Utils.RendererExtensions", "GetMutableMesh", 2)
                ?? FindExtension("VF.Utils.SkinnedMeshRendererExtensions", "GetMutableMesh", 2),
                "skin.GetMutableMesh(reason)");
            skinOwner = ReflectionUtils.Demand(
                FindExtension("VF.Utils.VFGameObjectExtensions", "owner", 1),
                "component.owner()");
            ownerGetPath = ReflectionUtils.Demand(
                vfGameObjectType.GetMethods(any)
                    .SingleOrDefault(method => method.Name == "GetPath"
                                               && method.GetParameters().Length == 2
                                               && method.GetParameters()[0].ParameterType == vfGameObjectType),
                "VFGameObject.GetPath(root, prettyRoot)");
            isMaybeMmdBlendshape = ReflectionUtils.Demand(
                ReflectionUtils.FindUniqueMethod(mmdUtilsType, "IsMaybeMmdBlendshape",
                    method => method.GetParameters().Length == 1),
                "MmdUtils.IsMaybeMmdBlendshape(name)");
            meshDirty = FindExtension("VF.Utils.UnityCompatUtils", "Dirty", 1)
                        ?? FindExtension("VF.Utils.ObjectExtensions", "Dirty", 1);

            var apply = ReflectionUtils.Demand(
                ReflectionUtils.FindNoArgVoid(builderType, "Apply"), "BlendshapeOptimizerBuilder.Apply()");
            harmony.Patch(
                apply,
                prefix: new HarmonyMethod(typeof(BlendshapeBakeRewritePatch), nameof(ApplyPrefix))
            );
        }

        private static MethodInfo FindExtension(string typeName, string methodName, int paramCount) {
            var type = ReflectionUtils.FindType(typeName);
            if (type == null) return null;
            return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == methodName
                                          && method.GetParameters().Length == paramCount);
        }

        private static bool ApplyPrefix(object __instance) {
            if (BlendshapeBakeRewriteModule.Instance?.Enabled != true) return true;
            try {
                Run(__instance);
                return false;
            } catch (Exception e) {
                Log.Warn("Fast blendshape bake fell back to VRCFury: " + e);
                return true;
            }
        }

        private static void Run(object builder) {
            var globals = globalsField.GetValue(builder);
            var keepMmdShapes = ((IEnumerable)allFeaturesField.GetValue(globals)).Cast<object>()
                .Any(feature => mmdCompatibilityType.IsInstanceOfType(feature));
            var avatarObject = avatarObjectField.GetValue(builder);
            var avatar = (VRCAvatarDescriptor)avatarField.GetValue(builder);
            var avatarRoot = VfGameObjectCompat.Unwrap(avatarObject);
            if (avatarRoot == null) throw new Exception("no avatar root");

            // Phase-boundary pref read, hoisted out of the per-shape bake loop.
            var fixInterpolation = Settings.IsOptionEnabled(
                BlendshapeBakeRewriteModule.Instance,
                BlendshapeBakeRewriteModule.FixMultiFrameInterpolation);

            // (a) HOISTED: skin-invariant animated-binding collection, one pass.
            var controllersService = controllersField.GetValue(builder);
            var animatorsService = animatorsField.GetValue(builder);
            var animatedBindings = new List<(EditorCurveBinding Binding, AnimationCurve Curve)>();
            void AddBindings(object owner, object controller) {
                var bindings = (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                    getBindings, builder, new[] { owner, controller });
                foreach (var entry in bindings) {
                    if (bindingsItem1 == null) {
                        var entryType = entry.GetType();
                        bindingsItem1 = entryType.GetField("Item1");
                        bindingsItem2 = entryType.GetField("Item2");
                    }
                    var binding = (EditorCurveBinding)bindingsItem1.GetValue(entry);
                    var curve = (AnimationCurve)bindingsItem2.GetValue(entry);
                    animatedBindings.Add((binding, curve));
                }
            }
            foreach (var manager in (IEnumerable)getAllUsedControllers.Invoke(controllersService, null)) {
                AddBindings(avatarObject, manager);
            }
            foreach (var pair in (IEnumerable)getSubControllers.Invoke(animatorsService, null)) {
                if (pairItem1 == null) {
                    var pairType = pair.GetType();
                    pairItem1 = pairType.GetField("Item1");
                    pairItem2 = pairType.GetField("Item2");
                }
                AddBindings(pairItem1.GetValue(pair), pairItem2.GetValue(pair));
            }

            // Bucket the skin-invariant blendshape bindings by path once, instead of
            // rescanning the full binding list for every skinned mesh below.
            var blendshapeBindingsByPath = new Dictionary<string, List<(string Blendshape, AnimationCurve Curve)>>();
            foreach (var (binding, curve) in animatedBindings) {
                if (binding.type != typeof(SkinnedMeshRenderer)) continue;
                if (!binding.propertyName.StartsWith("blendShape.")) continue;
                blendshapeBindingsByPath.GetOrAddList(binding.path)
                    .Add((binding.propertyName.Substring(11), curve));
            }

            var logOutput = new StringBuilder();
            var skins = (IEnumerable)vfGetComponentsInSelfAndChildren.Invoke(avatarObject, null);
            foreach (SkinnedMeshRenderer skin in skins) {
                var mesh = skinGetMesh.Invoke(null, new object[] { skin }) as Mesh;
                if (mesh == null) continue;
                var blendshapeCount = mesh.blendShapeCount;
                if (blendshapeCount == 0) continue;
                var skinOwnerObj = skinOwner.Invoke(null, new object[] { skin });
                var path = (string)ReflectionUtils.InvokeUnwrapped(
                    ownerGetPath, skinOwnerObj, new[] { avatarObjectField.GetValue(builder), (object)false });

                logOutput.Append($"\n┬─ Optimizing {path}\n");

                var animatedBlendshapes = CollectAnimatedBlendshapesForMesh(
                    skin, path, blendshapeBindingsByPath, avatar);

                bool ShouldKeepName(string name) {
                    if (animatedBlendshapes.Contains(name)) return true;
                    if (keepMmdShapes
                        && (bool)isMaybeMmdBlendshape.Invoke(null, new object[] { name })
                        && path == "Body") {
                        return true;
                    }
                    return false;
                }

                var blendshapeIdsToKeep = Enumerable.Range(0, blendshapeCount)
                    .Where(id => ShouldKeepName(mesh.GetBlendShapeName(id)))
                    .ToImmutableHashSetCompat();

                if (blendshapeIdsToKeep.Count == blendshapeCount) {
                    continue;
                }

                var savedWeights = Enumerable.Range(0, blendshapeCount)
                    .Select(skin.GetBlendShapeWeight).ToArray();

                // (b)+(c): the original mesh keeps all shape data after the mutable clone.
                var originalMesh = mesh;
                mesh = skinGetMutableMesh.Invoke(null,
                    new object[] { skin, "Needed to remove blendshapes for blendshape optimizer" }) as Mesh;
                if (mesh == null) throw new Exception("GetMutableMesh returned null");

                // GetMutableMesh returns the SAME object when the mesh was already made
                // mutable earlier in the build (bounding-box fix, SPS, …) — in that case
                // clearing would destroy our data source, so kept shapes are extracted
                // up front (only for this case, and only the KEPT shapes).
                var sameObject = ReferenceEquals(originalMesh, mesh);
                var vertexCount = mesh.vertexCount;
                var bufferV = new Vector3[vertexCount];
                var bufferN = new Vector3[vertexCount];
                var bufferT = new Vector3[vertexCount];
                var verts = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var bakedAny = false;

                // Phase A (pre-clear): capture metadata, bake non-kept shapes into the
                // accumulators, and extract kept frames when the source will be cleared.
                var names = new string[blendshapeCount];
                var frameCounts = new int[blendshapeCount];
                var keptFrames = sameObject
                    ? new Dictionary<int, List<(float Weight, Vector3[] V, Vector3[] N, Vector3[] T)>>()
                    : null;
                for (var id = 0; id < blendshapeCount; id++) {
                    names[id] = originalMesh.GetBlendShapeName(id);
                    frameCounts[id] = originalMesh.GetBlendShapeFrameCount(id);
                    var keep = blendshapeIdsToKeep.Contains(id);
                    if (!keep) {
                        BakeShape(originalMesh, id, frameCounts[id], savedWeights[id], fixInterpolation,
                            verts, normals, tangents, bufferV, bufferN, bufferT, ref bakedAny);
                    } else if (sameObject) {
                        var frames = new List<(float, Vector3[], Vector3[], Vector3[])>();
                        for (var frame = 0; frame < frameCounts[id]; frame++) {
                            var v = new Vector3[vertexCount];
                            var n = new Vector3[vertexCount];
                            var t = new Vector3[vertexCount];
                            originalMesh.GetBlendShapeFrameVertices(id, frame, v, n, t);
                            frames.Add((originalMesh.GetBlendShapeFrameWeight(id, frame), v, n, t));
                        }
                        keptFrames[id] = frames;
                    }
                }

                mesh.ClearBlendShapes();

                // Phase B (post-clear): re-add kept shapes in original id order.
                for (var id = 0; id < blendshapeCount; id++) {
                    var keep = blendshapeIdsToKeep.Contains(id);
                    string logOutputDetail;
                    if (keep) {
                        logOutputDetail = $"Keeping BlendShape \"{names[id]}\"\n";
                        if (sameObject) {
                            foreach (var (weight, v, n, t) in keptFrames[id]) {
                                mesh.AddBlendShapeFrame(names[id], weight, v, n, t);
                            }
                        } else {
                            for (var frame = 0; frame < frameCounts[id]; frame++) {
                                var weight = originalMesh.GetBlendShapeFrameWeight(id, frame);
                                originalMesh.GetBlendShapeFrameVertices(id, frame, bufferV, bufferN, bufferT);
                                mesh.AddBlendShapeFrame(names[id], weight, bufferV, bufferN, bufferT);
                            }
                        }
                    } else {
                        logOutputDetail =
                            $"Baking BlendShape \"{names[id]}\" into mesh at weight {savedWeights[id]}, as weight is not animated\n";
                    }
                    logOutput.Append(id != blendshapeCount - 1 ? "├" : "└").Append(logOutputDetail);
                }

                if (bakedAny) {
                    mesh.vertices = verts;
                    mesh.normals = normals;
                    mesh.tangents = tangents;
                }
                if (meshDirty != null) meshDirty.Invoke(null, new object[] { mesh });
                else EditorUtility.SetDirty(mesh);

                var newId = 0;
                for (var id = 0; id < blendshapeCount; id++) {
                    var keep = blendshapeIdsToKeep.Contains(id);
                    if (keep) {
                        skin.SetBlendShapeWeight(newId, savedWeights[id]);
                        if (avatar.customEyeLookSettings.eyelidsSkinnedMesh == skin) {
                            for (var i = 0; i < avatar.customEyeLookSettings.eyelidsBlendshapes.Length; i++) {
                                if (avatar.customEyeLookSettings.eyelidsBlendshapes[i] == id) {
                                    avatar.customEyeLookSettings.eyelidsBlendshapes[i] = newId;
                                    EditorUtility.SetDirty(avatar);
                                }
                            }
                        }
                        newId++;
                    }
                }
            }
            Debug.Log($"Blendshape Optimizer Actions:\n{logOutput}");
        }

        /**
         * Replicates SavedBlendshape.BakeTo exactly (including the upstream multi-frame
         * selection oddity `weight100 <= frames.Count`), accumulating into the shared
         * arrays instead of round-tripping the mesh per shape.
         */
        private static void BakeShape(
            Mesh originalMesh, int id, int frameCount, float weight100, bool fix,
            Vector3[] verts, Vector3[] normals, Vector4[] tangents,
            Vector3[] bufferV, Vector3[] bufferN, Vector3[] bufferT,
            ref bool bakedAny
        ) {
            if (frameCount == 0 || weight100 == 0) {
                return;
            }
            var lastFrameWeight = originalMesh.GetBlendShapeFrameWeight(id, frameCount - 1);
            if (frameCount == 1 || weight100 < 0 || weight100 >= lastFrameWeight) {
                originalMesh.GetBlendShapeFrameVertices(id, frameCount - 1, bufferV, bufferN, bufferT);
                Accumulate(verts, normals, tangents, bufferV, bufferN, bufferT, weight100);
                bakedAny = true;
            } else {
                int beforeFrame;
                if (fix) {
                    // Intended semantics: first frame whose weight reaches the target.
                    // Guaranteed to exist here (weight100 < lastFrameWeight).
                    beforeFrame = Enumerable.Range(0, frameCount)
                        .First(frame => weight100 <= originalMesh.GetBlendShapeFrameWeight(id, frame));
                } else {
                    // Stock: First(frame => frame == frames.Count || weight100 <= frames.Count)
                    // — compares against the COUNT, not the frame weight (and throws when the
                    // weight exceeds the count). Replicated for stock-identical output.
                    beforeFrame = Enumerable.Range(0, frameCount)
                        .First(frame => frame == frameCount || weight100 <= frameCount);
                }
                if (beforeFrame == 0) {
                    var fw = originalMesh.GetBlendShapeFrameWeight(id, 0);
                    originalMesh.GetBlendShapeFrameVertices(id, 0, bufferV, bufferN, bufferT);
                    Accumulate(verts, normals, tangents, bufferV, bufferN, bufferT, weight100 / fw);
                    bakedAny = true;
                } else {
                    var fw1 = originalMesh.GetBlendShapeFrameWeight(id, beforeFrame - 1);
                    var fw2 = originalMesh.GetBlendShapeFrameWeight(id, beforeFrame);
                    var fraction = (weight100 - fw1) / (fw2 - fw1);
                    var v1 = new Vector3[verts.Length];
                    var n1 = new Vector3[normals.Length];
                    var t1 = new Vector3[verts.Length];
                    originalMesh.GetBlendShapeFrameVertices(id, beforeFrame - 1, v1, n1, t1);
                    originalMesh.GetBlendShapeFrameVertices(id, beforeFrame, bufferV, bufferN, bufferT);
                    for (var i = 0; i < verts.Length; i++) {
                        bufferV[i] = v1[i] + (bufferV[i] - v1[i]) * fraction;
                    }
                    for (var i = 0; i < normals.Length; i++) {
                        bufferN[i] = n1[i] + (bufferN[i] - n1[i]) * fraction;
                    }
                    for (var i = 0; i < verts.Length; i++) {
                        bufferT[i] = t1[i] + (bufferT[i] - t1[i]) * fraction;
                    }
                    Accumulate(verts, normals, tangents, bufferV, bufferN, bufferT, 100);
                    bakedAny = true;
                }
            }
        }

        private static void Accumulate(
            Vector3[] verts, Vector3[] normals, Vector4[] tangents,
            Vector3[] dv, Vector3[] dn, Vector3[] dt, float weight100
        ) {
            var scale = weight100 / 100;
            for (var i = 0; i < verts.Length && i < dv.Length; i++) {
                verts[i] += dv[i] * scale;
            }
            for (var i = 0; i < normals.Length && i < dn.Length; i++) {
                normals[i] += dn[i] * scale;
            }
            for (var i = 0; i < tangents.Length && i < dt.Length; i++) {
                var d = dt[i] * scale;
                tangents[i] += new Vector4(d.x, d.y, d.z, 0);
            }
        }

        private static ISet<int> ToImmutableHashSetCompat(this IEnumerable<int> source) {
            return new HashSet<int>(source);
        }

        private static HashSet<string> CollectAnimatedBlendshapesForMesh(
            SkinnedMeshRenderer skin,
            string skinPath,
            Dictionary<string, List<(string Blendshape, AnimationCurve Curve)>> blendshapeBindingsByPath,
            VRCAvatarDescriptor avatar
        ) {
            var animatedBlendshapes = new HashSet<string>();
            var mesh = skin.sharedMesh;
            if (blendshapeBindingsByPath.TryGetValue(skinPath, out var bindings)) {
                foreach (var (blendshape, curve) in bindings) {
                    var index = mesh != null ? mesh.GetBlendShapeIndex(blendshape) : -1;
                    if (index < 0) continue;
                    var skinDefaultValue = skin.GetBlendShapeWeight(index);
                    foreach (var key in curve.keys) {
                        if (!Mathf.Approximately(key.value, skinDefaultValue)) {
                            animatedBlendshapes.Add(blendshape);
                            break;
                        }
                    }
                }
            }

            if (avatar.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes) {
                if (skin == avatar.customEyeLookSettings.eyelidsSkinnedMesh) {
                    foreach (var b in avatar.customEyeLookSettings.eyelidsBlendshapes) {
                        if (mesh != null && b >= 0 && b < mesh.blendShapeCount) {
                            animatedBlendshapes.Add(mesh.GetBlendShapeName(b));
                        }
                    }
                }
            }

            if (skin == avatar.VisemeSkinnedMesh) {
                if (avatar.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape) {
                    animatedBlendshapes.Add(avatar.MouthOpenBlendShapeName);
                }
                if (avatar.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape) {
                    foreach (var b in avatar.VisemeBlendShapes) {
                        animatedBlendshapes.Add(b);
                    }
                }
            }

            return animatedBlendshapes;
        }
    }
}
