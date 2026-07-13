using System;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace FuryPlusPlus {
    /**
     * Base class for every FuryPlusPlus standalone pass. The VRCSDK auto-discovers
     * IVRCSDKPreprocessAvatarCallback implementers regardless of our bootstrap, so passes
     * cannot be conditionally registered — this base is the guard: registry/enabled checks
     * at run time and a fail-open try/catch. A pass failure must NEVER block a build.
     */
    internal abstract class GuardedPreprocessorPass : IVRCSDKPreprocessAvatarCallback {
        public abstract int callbackOrder { get; }

        /** Module gating this pass; null = the pass manages per-module gating itself. */
        protected virtual Module GatingModule => null;

        protected abstract bool Run(GameObject avatarObject);

        public bool OnPreprocessAvatar(GameObject avatarObject) {
            try {
                if (!Settings.MasterEnabled) return true;
                var module = GatingModule;
                if (module != null && (!ModuleRegistry.IsActive(module) || !module.Enabled)) {
                    return true;
                }
                return Run(avatarObject);
            } catch (Exception e) {
                Log.Error(GetType().Name + " failed and was skipped: " + e);
                return true;
            }
        }
    }
}
