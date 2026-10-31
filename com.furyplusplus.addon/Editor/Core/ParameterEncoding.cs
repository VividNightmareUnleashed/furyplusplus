using VRC.SDK3.Avatars.ScriptableObjects;

namespace FuryPlusPlus {
    /**
     * Network encoding of an expression parameter. VRC SDK 3.10.6 lets Int and Float
     * parameters choose their bit width and signedness; earlier SDKs always send 8 bits.
     */
    internal static class ParameterEncoding {
        /** Sync bits the parameter costs (Bool 1, Int/Float 8 unless a custom width is set). */
        internal static int BitCost(VRCExpressionParameters.Parameter parameter) {
#if VRCSDK_HAS_DYNAMIC_PARAMETER_BIT_WIDTH
            return parameter.GetBitWidthByType();
#else
            return VRCExpressionParameters.TypeCost(parameter.valueType);
#endif
        }

        /** Same test as VRCFury's HasStandardNetworkEncoding: 8 bits, default signedness. */
        internal static bool IsStandard(VRCExpressionParameters.Parameter parameter) {
            if (parameter.valueType == VRCExpressionParameters.ValueType.Bool) return true;
            if (parameter.valueType != VRCExpressionParameters.ValueType.Int
                && parameter.valueType != VRCExpressionParameters.ValueType.Float) return false;
#if VRCSDK_HAS_DYNAMIC_PARAMETER_BIT_WIDTH
            return parameter.GetBitWidthByType() == 8 && !parameter.flippedRepresentation;
#else
            return true;
#endif
        }
    }
}
