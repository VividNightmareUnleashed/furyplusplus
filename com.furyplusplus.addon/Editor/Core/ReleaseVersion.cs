using System;

namespace FuryPlusPlus {
    internal static class ReleaseVersion {
        internal static bool IsStable(string value) {
            return value != null && value.Length <= 32 && Version.TryParse(value, out var version)
                   && version.Build >= 0 && version.Revision == -1 && version.ToString(3) == value;
        }
    }
}
