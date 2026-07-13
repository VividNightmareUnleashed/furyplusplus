namespace FuryPlusPlus {
    internal static class Log {
        private const string Prefix = "[FuryPlusPlus] ";

        internal static void Info(string message) {
            UnityEngine.Debug.Log(Prefix + message);
        }

        internal static void Warn(string message) {
            UnityEngine.Debug.LogWarning(Prefix + message);
        }

        internal static void Error(string message) {
            UnityEngine.Debug.LogError(Prefix + message);
        }
    }
}
