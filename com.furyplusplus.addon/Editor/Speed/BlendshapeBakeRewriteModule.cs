using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
            "Selects interpolation frames by their weights and corrects the extra division " +
            "by 100 below the first frame. Disable for stock-identical behavior, including " +
            "VRCFury's frame-selection and scaling bugs.",
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
        internal static void Install(Harmony harmony, VrcfuryCompat compatibility) {
            BlendshapeOptimizerCompat.DemandCore();
            ClipCurveCompat.EnsureResolved();
            ReflectionUtils.Demand(ClipCurveCompat.BindingTarget, "VFBinding.target");
            ReflectionUtils.Demand(ClipCurveCompat.BindingPropertyName, "VFBinding.propertyName");
            VfGameObjectCompat.DemandCore();
            harmony.Patch(
                BlendshapeOptimizerCompat.Apply,
                prefix: new HarmonyMethod(typeof(BlendshapeBakeRewritePatch), nameof(ApplyPrefix))
            );
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
            var globals = BlendshapeOptimizerCompat.Globals.GetValue(builder);
            var keepMmdShapes = ((IEnumerable)BlendshapeOptimizerCompat.AllFeatures.GetValue(globals))
                .Cast<object>()
                .Any(feature => BlendshapeOptimizerCompat.MmdCompatibilityType.IsInstanceOfType(feature));
            var avatarObject = BlendshapeOptimizerCompat.AvatarObject.GetValue(builder);
            var avatar = (VRCAvatarDescriptor)BlendshapeOptimizerCompat.Avatar.GetValue(builder);
            var avatarRoot = VfGameObjectCompat.Unwrap(avatarObject);
            if (avatarRoot == null) throw new Exception("no avatar root");

            // Phase-boundary pref read, hoisted out of the per-shape bake loop.
            var fixInterpolation = Settings.IsOptionEnabled(
                BlendshapeBakeRewriteModule.Instance,
                BlendshapeBakeRewriteModule.FixMultiFrameInterpolation);

            var blendshapeBindingsByTarget = BuildBlendshapeBindingIndex(builder);

            var logOutput = new StringBuilder();
            var skins = (IEnumerable)BlendshapeOptimizerCompat.GetSkins.Invoke(avatarObject, null);
            foreach (SkinnedMeshRenderer skin in skins) {
                OptimizeSkin(builder, skin, avatar, keepMmdShapes, fixInterpolation,
                    blendshapeBindingsByTarget, logOutput);
            }
            Debug.Log($"Blendshape Optimizer Actions:\n{logOutput}");
        }

        private static void OptimizeSkin(
            object builder,
            SkinnedMeshRenderer skin,
            VRCAvatarDescriptor avatar,
            bool keepMmdShapes,
            bool fixInterpolation,
            Dictionary<object, List<(string Blendshape, AnimationCurve Curve)>>
                blendshapeBindingsByTarget,
            StringBuilder logOutput
        ) {
            var mesh = BlendshapeOptimizerCompat.SkinGetMesh.Invoke(
                null, new object[] { skin }) as Mesh;
            if (mesh == null) return;
            var blendshapeCount = mesh.blendShapeCount;
            if (blendshapeCount == 0) return;
            var skinOwnerObj = BlendshapeOptimizerCompat.SkinOwner.Invoke(
                null, new object[] { skin });
            var path = (string)ReflectionUtils.InvokeUnwrapped(
                BlendshapeOptimizerCompat.OwnerGetPath, skinOwnerObj,
                new[] { BlendshapeOptimizerCompat.AvatarObject.GetValue(builder),
                    (object)false, (object)false });

            logOutput.Append($"\n┬─ Optimizing {path}\n");

            var animatedBlendshapes = CollectAnimatedBlendshapesForMesh(
                skin, skinOwnerObj, blendshapeBindingsByTarget, avatar);

            bool ShouldKeepName(string name) {
                if (animatedBlendshapes.Contains(name)) return true;
                if (keepMmdShapes
                    && (bool)BlendshapeOptimizerCompat.IsMaybeMmdBlendshape.Invoke(
                        null, new object[] { name })
                    && path == "Body") {
                    return true;
                }
                return false;
            }

            var blendshapeIdsToKeep = Enumerable.Range(0, blendshapeCount)
                .Where(id => ShouldKeepName(mesh.GetBlendShapeName(id)))
                .ToImmutableHashSetCompat();

            if (blendshapeIdsToKeep.Count == blendshapeCount) return;

            var savedWeights = Enumerable.Range(0, blendshapeCount)
                .Select(skin.GetBlendShapeWeight).ToArray();

            // The original mesh keeps all shape data after the mutable clone.
            var originalMesh = mesh;
            mesh = BlendshapeOptimizerCompat.SkinGetMutableMesh.Invoke(null,
                new object[] { skin, "Needed to remove blendshapes for blendshape optimizer" }) as Mesh;
            if (mesh == null) throw new Exception("GetMutableMesh returned null");

            // GetMutableMesh can return the same object when an earlier pass already made
            // the mesh mutable. Capture kept frames before clearing in that case.
            var sameObject = ReferenceEquals(originalMesh, mesh);
            var vertexCount = mesh.vertexCount;
            var bufferV = new Vector3[vertexCount];
            var bufferN = new Vector3[vertexCount];
            var bufferT = new Vector3[vertexCount];
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var bakedAny = false;

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

            for (var id = 0; id < blendshapeCount; id++) {
                var keep = blendshapeIdsToKeep.Contains(id);
                string detail;
                if (keep) {
                    detail = $"Keeping BlendShape \"{names[id]}\"\n";
                    if (sameObject) {
                        foreach (var (weight, v, n, t) in keptFrames[id]) {
                            mesh.AddBlendShapeFrame(names[id], weight, v, n, t);
                        }
                    } else {
                        for (var frame = 0; frame < frameCounts[id]; frame++) {
                            var weight = originalMesh.GetBlendShapeFrameWeight(id, frame);
                            originalMesh.GetBlendShapeFrameVertices(
                                id, frame, bufferV, bufferN, bufferT);
                            mesh.AddBlendShapeFrame(names[id], weight, bufferV, bufferN, bufferT);
                        }
                    }
                } else {
                    detail = $"Baking BlendShape \"{names[id]}\" into mesh at weight " +
                             $"{savedWeights[id]}, as weight is not animated\n";
                }
                logOutput.Append(id != blendshapeCount - 1 ? "├" : "└").Append(detail);
            }

            if (bakedAny) {
                mesh.vertices = verts;
                mesh.normals = normals;
                mesh.tangents = tangents;
            }
            if (BlendshapeOptimizerCompat.MeshDirty != null) {
                BlendshapeOptimizerCompat.MeshDirty.Invoke(null, new object[] { mesh });
            } else {
                EditorUtility.SetDirty(mesh);
            }

            RestoreWeightsAndEyelids(
                skin, avatar, blendshapeCount, blendshapeIdsToKeep, savedWeights);
        }

        /** Buckets the skin-invariant curve scan by resolved renderer owner. */
        private static Dictionary<object, List<(string Blendshape, AnimationCurve Curve)>>
            BuildBlendshapeBindingIndex(object builder) {
            var bindingsByTarget =
                new Dictionary<object, List<(string Blendshape, AnimationCurve Curve)>>();

            void AddCurves(object controller) {
                var curves = (IEnumerable)ReflectionUtils.InvokeUnwrapped(
                    BlendshapeOptimizerCompat.GetBlendshapeCurves, builder, new[] { controller });
                foreach (var entry in curves) {
                    var binding = BlendshapeOptimizerCompat.BindingOf(entry);
                    var curve = BlendshapeOptimizerCompat.CurveOf(entry);
                    var target = ClipCurveCompat.TargetOf(binding);
                    if (target == null || curve == null) continue;
                    var propertyName = ClipCurveCompat.PropertyNameOf(binding);
                    if (propertyName == null || !propertyName.StartsWith("blendShape.")) continue;
                    bindingsByTarget.GetOrAddList(target)
                        .Add((propertyName.Substring(11), curve));
                }
            }

            var controllersService = BlendshapeOptimizerCompat.Controllers.GetValue(builder);
            foreach (var manager in (IEnumerable)BlendshapeOptimizerCompat.GetAllUsedControllers
                         .Invoke(controllersService, null)) {
                AddCurves(manager);
            }
            var animatorsService = BlendshapeOptimizerCompat.Animators.GetValue(builder);
            foreach (var pair in (IEnumerable)BlendshapeOptimizerCompat.GetSubControllers
                         .Invoke(animatorsService, null)) {
                AddCurves(BlendshapeOptimizerCompat.ControllerOf(pair));
            }
            return bindingsByTarget;
        }

        private static void RestoreWeightsAndEyelids(
            SkinnedMeshRenderer skin,
            VRCAvatarDescriptor avatar,
            int originalCount,
            ISet<int> keptIds,
            IReadOnlyList<float> savedWeights
        ) {
            var newId = 0;
            for (var id = 0; id < originalCount; id++) {
                if (!keptIds.Contains(id)) continue;
                skin.SetBlendShapeWeight(newId, savedWeights[id]);
                if (avatar.customEyeLookSettings.eyelidsSkinnedMesh == skin) {
                    for (var i = 0; i < avatar.customEyeLookSettings.eyelidsBlendshapes.Length; i++) {
                        if (avatar.customEyeLookSettings.eyelidsBlendshapes[i] != id) continue;
                        avatar.customEyeLookSettings.eyelidsBlendshapes[i] = newId;
                        EditorUtility.SetDirty(avatar);
                    }
                }
                newId++;
            }
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
                    Accumulate(verts, normals, tangents, bufferV, bufferN, bufferT,
                        weight100 / fw * (fix ? 100f : 1f));
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

        /**
         * Mirrors VRCFury's CollectAnimatedBlendshapesForMesh. Bindings are matched by their
         * resolved target object (VFBinding.Targets), not by path string — object identity is
         * what VRCFury itself compares now, and it stays correct across renames and moves.
         */
        private static HashSet<string> CollectAnimatedBlendshapesForMesh(
            SkinnedMeshRenderer skin,
            object skinOwnerObj,
            Dictionary<object, List<(string Blendshape, AnimationCurve Curve)>> blendshapeBindingsByTarget,
            VRCAvatarDescriptor avatar
        ) {
            var animatedBlendshapes = new HashSet<string>();
            var mesh = skin.sharedMesh;
            if (skinOwnerObj != null
                && blendshapeBindingsByTarget.TryGetValue(skinOwnerObj, out var bindings)) {
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
