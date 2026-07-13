using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FuryPlusPlus {
    internal enum ModuleKind {
        /** Output-identical bake speedups (Harmony patches on VRCFury internals). */
        Speed,
        /** Deliberately changes VRCFury's bake output (Harmony patches on VRCFury internals). */
        Quality,
        /** Standalone IVRCSDKPreprocessAvatarCallback pass; must derive from GuardedPreprocessorPass. */
        Pass,
        /** Editor-visual only (e.g. progress window theming); never affects bake output. */
        Cosmetic
    }

    internal enum CompatTier {
        /** VRCFury-Editor-Avatars loaded and the four profiling members resolvable (any version). */
        Profiling,
        /** Depends only on public VRC SDK types; active even on unknown VRCFury versions. */
        PublicSdk,
        /** Reflection/Harmony on VRCFury internals; pinned to VrcfuryCompat.PinnedVersion, fail-closed. */
        ExactVersion
    }

    internal abstract class Module {
        /** Stable identifier; used as the EditorPrefs key stem and stats key. Lowercase camelCase, no dots. */
        internal abstract string Id { get; }
        internal abstract string DisplayName { get; }
        internal abstract ModuleKind Kind { get; }
        internal virtual string Description => "";
        internal virtual bool DefaultEnabled => true;

        internal virtual CompatTier RequiredTier =>
            Kind == ModuleKind.Pass ? CompatTier.PublicSdk : CompatTier.ExactVersion;

        internal virtual IReadOnlyList<ModuleOption> Options => Array.Empty<ModuleOption>();

        /**
         * Runtime kill switch (master switch + per-module pref). Patches read this at phase
         * boundaries (Begin-style prefixes), never per hot inner call.
         */
        internal bool Enabled => Settings.IsModuleEnabled(this);

        /**
         * Fail-closed: throw (MissingMemberException or similar) on ANY unresolved reflection
         * target. The registry catches, marks the module Failed, and moves on.
         */
        internal abstract void Install(Harmony harmony, VrcfuryCompat compat);

        /** One-line stats for the profiler report footer; null = nothing to report. */
        internal virtual string ReportStats() => null;
    }
}
