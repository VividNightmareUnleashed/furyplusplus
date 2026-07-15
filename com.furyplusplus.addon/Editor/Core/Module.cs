using System;
using System.Collections.Generic;
using System.Globalization;
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

    /**
     * A pointer to a VRCFury-native equivalent of one of our modules — the version that
     * shipped it, a one-line note, and the upstream commit. Used two ways (see Module):
     * Superseded (VRCFury's is equal-or-better, ours removed) and OverridesNative (ours is
     * faster and takes over).
     */
    internal readonly struct NativeEquivalent {
        /** VRCFury version that shipped the native equivalent, e.g. "1.1364.0". */
        internal readonly string Version;
        /** One line on the relationship (what VRCFury does / why ours wins). */
        internal readonly string Note;
        /** Full github.com commit URL for the native implementation. */
        internal readonly string CommitUrl;

        internal NativeEquivalent(string version, string note, string commitUrl) {
            Version = version;
            Note = note;
            CommitUrl = commitUrl;
        }
    }

    internal abstract class Module {
        /** Stable identifier; used as the EditorPrefs key stem and stats key. Lowercase camelCase, no dots. */
        internal abstract string Id { get; }
        internal abstract string DisplayName { get; }
        internal abstract ModuleKind Kind { get; }
        internal virtual string Description => "";
        internal virtual bool DefaultEnabled => true;

        /**
         * Non-null marks a module VRCFury now performs natively (benchmarked equal-or-better
         * on the pinned version). The registry never installs it and the settings window
         * renders it struck through with this note. Its patch code has been removed.
         */
        internal virtual NativeEquivalent? Superseded => null;

        /**
         * Non-null marks a still-installed module whose optimization VRCFury also added
         * natively (later), but which the benchmark still favors — we keep our faster path and
         * bypass VRCFury's. The settings window notes this next to the (active) toggle.
         */
        internal virtual NativeEquivalent? OverridesNative => null;

        internal virtual CompatTier RequiredTier =>
            Kind == ModuleKind.Pass ? CompatTier.PublicSdk : CompatTier.ExactVersion;

        internal virtual IReadOnlyList<ModuleOption> Options => Array.Empty<ModuleOption>();

        /** Semicolon-separated wildcard-list settings; always bake-output-affecting. */
        internal virtual IReadOnlyList<ModuleListOption> ListOptions => Array.Empty<ModuleListOption>();

        /** Settings-window group title within this module's kind tab; null lands in "Other". */
        internal virtual string SettingsGroup => null;

        /**
         * Runtime kill switch (master switch + per-module pref). Patches read this at phase
         * boundaries (Begin-style prefixes), never per hot inner call.
         */
        internal bool Enabled => Settings.IsModuleEnabled(this);

        /** Installed for this domain load AND currently switched on. */
        internal bool IsActiveAndEnabled => ModuleRegistry.IsActive(this) && Enabled;

        // Id is immutable and Enabled runs at every phase boundary — never rebuild the key.
        private string prefKey;
        internal string PrefKey => prefKey ?? (prefKey = Settings.KeyPrefix + "module." + Id);

        /**
         * Fail-closed: throw (MissingMemberException or similar) on ANY unresolved reflection
         * target. The registry catches, marks the module Failed, and moves on.
         */
        internal abstract void Install(Harmony harmony, VrcfuryCompat compat);

        /** One-line stats for the profiler report footer; null = nothing to report. */
        internal virtual string ReportStats() => null;

        /**
         * Green gain chip for this module's settings row: a real measured or projected
         * number (the same typed counters ReportStats formats), never an invented estimate.
         * Null = no chip. `analysis` is the window's last per-avatar projection, if any.
         */
        internal virtual (string Text, string Tooltip)? ReportGain(Estimators.Result? analysis) => null;

        /** Module-specific extra controls rendered under the settings row while installed. */
        internal virtual void DrawExtraSettings() { }

        protected static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /**
     * Module with a registry-constructed singleton. Patch classes reach their module via
     * TSelf.Instance instead of every module hand-rolling the property (where a forgotten
     * assignment silently disables the module).
     */
    internal abstract class Module<TSelf> : Module where TSelf : Module<TSelf> {
        internal static TSelf Instance { get; private set; }

        protected Module() {
            Instance = (TSelf)this;
        }
    }
}
