using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Read-only, per-avatar improvement projections for the welcome/settings window —
     * dry runs of the quality passes' own detection logic against the UNBAKED scene
     * avatar. Numbers are estimates: the baked result differs once VRCFury adds its own
     * parameters and layers. Each estimator is exception-isolated; a failure reports -1.
     */
    internal static class Estimators {
        internal struct Result {
            internal int SyncedBits;
            internal int MaxBits;
            internal int StrippableParams;
            internal int StrippableBits;
            internal int NarrowableInts;
            internal int FxLayers;
            internal int NonAnimatedBlendshapes;
            internal int BlendshapeMeshes;
            /** GPU bytes held by the non-animated blendshapes; -1 when unreadable. */
            internal long BlendshapeVramBytes;
        }

        internal static Result Analyze(VRCAvatarDescriptor descriptor) {
            var result = new Result {
                SyncedBits = -1, MaxBits = 256, StrippableParams = -1, StrippableBits = -1,
                NarrowableInts = -1, FxLayers = -1, NonAnimatedBlendshapes = -1, BlendshapeMeshes = -1,
                BlendshapeVramBytes = -1
            };

            ParamUsageIndex index = null;
            try {
                index = ParamUsageIndex.Build(descriptor);
            } catch {
                // estimators below that need it will report -1
            }

            try {
                var paramsAsset = descriptor.expressionParameters;
                if (paramsAsset != null && paramsAsset.parameters != null) {
                    result.SyncedBits = paramsAsset.CalcTotalCost();
                    if (index != null) {
                        var strippable = paramsAsset.parameters
                            .Where(parameter => parameter != null
                                                && parameter.networkSynced
                                                && !string.IsNullOrEmpty(parameter.name)
                                                && !index.Reads.Contains(parameter.name))
                            .ToList();
                        result.StrippableParams = strippable.Count;
                        result.StrippableBits = strippable
                            .Sum(parameter => VRCExpressionParameters.TypeCost(parameter.valueType));

                        result.NarrowableInts = paramsAsset.parameters.Count(parameter =>
                            parameter != null
                            && parameter.networkSynced
                            && parameter.valueType == VRCExpressionParameters.ValueType.Int
                            && !string.IsNullOrEmpty(parameter.name)
                            && (parameter.defaultValue == 0 || parameter.defaultValue == 1)
                            && index.Reads.Contains(parameter.name)
                            && index.Details.TryGetValue(parameter.name, out var detail)
                            && !detail.UsedAsPuppet
                            && !detail.MenuValueOtherThanOne
                            && !detail.HasUnsupportedCondition
                            && !detail.DriverNonBinaryWrite
                            && !detail.AapTarget
                            && (detail.HasMenuControl || detail.HasDriverWrite));
                    }
                }
            } catch { }

            try {
                var fx = descriptor.baseAnimationLayers
                    .FirstOrDefault(layer => layer.type == VRCAvatarDescriptor.AnimLayerType.FX);
                if (fx.animatorController is AnimatorController fxController) {
                    result.FxLayers = fxController.layers.Length;
                }
            } catch { }

            try {
                // Blendshapes with no animated 'blendShape.' binding anywhere — what the
                // Blendshape Optimizer would bake away. Count only; exact VRAM depends on
                // per-shape affected vertices.
                var animatedShapeKeys = new HashSet<(string Path, string Shape)>();
                foreach (var layer in descriptor.baseAnimationLayers
                             .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())) {
                    if (layer.isDefault || !(layer.animatorController is AnimatorController controller)) continue;
                    foreach (var clip in controller.animationClips) {
                        if (clip == null) continue;
                        foreach (var binding in AnimationUtility.GetCurveBindings(clip)) {
                            if (binding.type == typeof(SkinnedMeshRenderer)
                                && binding.propertyName.StartsWith("blendShape.")) {
                                animatedShapeKeys.Add((binding.path, binding.propertyName.Substring(11)));
                            }
                        }
                    }
                }
                var nonAnimated = 0;
                var meshes = 0;
                long vramBytes = 0;
                var vramKnown = true;
                var root = descriptor.transform;
                foreach (var skin in descriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                    var mesh = skin.sharedMesh;
                    if (mesh == null || mesh.blendShapeCount == 0) continue;
                    meshes++;
                    var path = AnimationUtility.CalculateTransformPath(skin.transform, root);
                    Vector3[] deltaVertices = null, deltaNormals = null, deltaTangents = null;
                    for (var id = 0; id < mesh.blendShapeCount; id++) {
                        if (animatedShapeKeys.Contains((path, mesh.GetBlendShapeName(id)))) continue;
                        nonAnimated++;
                        try {
                            if (deltaVertices == null) {
                                deltaVertices = new Vector3[mesh.vertexCount];
                                deltaNormals = new Vector3[mesh.vertexCount];
                                deltaTangents = new Vector3[mesh.vertexCount];
                            }
                            var frames = mesh.GetBlendShapeFrameCount(id);
                            for (var frame = 0; frame < frames; frame++) {
                                mesh.GetBlendShapeFrameVertices(id, frame, deltaVertices, deltaNormals, deltaTangents);
                                var affected = 0;
                                for (var v = 0; v < deltaVertices.Length; v++) {
                                    if (deltaVertices[v] != Vector3.zero || deltaNormals[v] != Vector3.zero
                                        || deltaTangents[v] != Vector3.zero) {
                                        affected++;
                                    }
                                }
                                // Unity's GPU skinning stores blendshapes sparsely: one entry
                                // per affected vertex per frame — float3 position + float3
                                // normal + float3 tangent + uint index = 40 bytes.
                                vramBytes += affected * 40L;
                            }
                        } catch {
                            vramKnown = false;
                        }
                    }
                }
                result.NonAnimatedBlendshapes = nonAnimated;
                result.BlendshapeMeshes = meshes;
                result.BlendshapeVramBytes = vramKnown ? vramBytes : -1;
            } catch { }

            return result;
        }
    }
}
