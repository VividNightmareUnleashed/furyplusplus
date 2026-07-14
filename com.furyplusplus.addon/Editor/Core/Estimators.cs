using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Read-only, per-avatar improvement projections for the welcome/settings window,
     * computed by the passes' OWN eligibility classifiers (StripUnusedParamsPass.Classify,
     * NarrowIntParamsPass.Classify) against the UNBAKED scene avatar — the projection
     * cannot drift from what the passes actually do. Numbers are still estimates: the
     * baked result differs once VRCFury adds its own parameters and layers. Each
     * estimator is exception-isolated; a failure reports -1.
     */
    internal static class Estimators {
        internal struct Result {
            internal int SyncedBits;
            internal int MaxBits;
            internal int StrippableParams;
            internal int StrippableBits;
            internal int NarrowableInts;
            internal int FxLayers;
        }

        internal static Result Analyze(VRCAvatarDescriptor descriptor) {
            var result = new Result {
                SyncedBits = -1, MaxBits = 256, StrippableParams = -1, StrippableBits = -1,
                NarrowableInts = -1, FxLayers = -1
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
                        // The passes' own doctrines, with the user's current settings —
                        // the strip runs first, so narrowing only sees the remainder.
                        var keepDynamics = Settings.IsOptionEnabled(
                            StripUnusedParamsModule.Instance, StripUnusedParamsModule.KeepDynamicsParams);
                        var keepGlobs = StripUnusedParamsModule.CurrentKeepGlobs();

                        var strippable = paramsAsset.parameters
                            .Where(parameter => StripUnusedParamsPass.Classify(
                                parameter, index, keepDynamics, keepGlobs)
                                == StripUnusedParamsPass.KeepReason.None)
                            .ToList();
                        result.StrippableParams = strippable.Count;
                        result.StrippableBits = strippable
                            .Sum(parameter => VRCExpressionParameters.TypeCost(parameter.valueType));

                        var strippedNames = new HashSet<string>(strippable.Select(p => p.name));
                        result.NarrowableInts = paramsAsset.parameters.Count(parameter =>
                            parameter != null
                            && !strippedNames.Contains(parameter.name)
                            && NarrowIntParamsPass.Classify(parameter, index, keepGlobs)
                                == NarrowIntParamsPass.Verdict.Eligible);
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

            return result;
        }
    }
}
