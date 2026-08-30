using UnityEditor.PackageManager;

namespace FuryPlusPlus {
    /** Package metadata shared by sidecars, snapshots, and UI. */
    internal static class PackageIdentity {
        private static string version;

        internal static string Version {
            get {
                if (version != null) return version;
                try {
                    version = PackageInfo.FindForAssembly(typeof(PackageIdentity).Assembly)?.version
                              ?? "unknown";
                } catch {
                    version = "unknown";
                }
                return version;
            }
        }
    }
}
