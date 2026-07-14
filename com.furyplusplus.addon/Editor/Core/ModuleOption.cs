namespace FuryPlusPlus {
    /** A per-module sub-toggle, stored at com.furyplusplus.module.&lt;moduleId&gt;.&lt;suffix&gt;. */
    internal sealed class ModuleOption {
        internal readonly string Suffix;
        internal readonly string Label;
        internal readonly bool Default;
        internal readonly string Description;
        /**
         * True when flipping this option changes the processed avatar (bug-fix sub-toggles,
         * play-mode skips), so it must invalidate the bake cache even on a Speed/Cosmetic
         * module. Options of Quality/Pass modules always count as output-affecting.
         */
        internal readonly bool AffectsBakeOutput;

        // Every option is declared inside exactly one module, so its key is stable.
        private string cachedKey;

        internal ModuleOption(string suffix, string label, bool defaultValue, string description = "",
            bool affectsBakeOutput = false) {
            Suffix = suffix;
            Label = label;
            Default = defaultValue;
            Description = description;
            AffectsBakeOutput = affectsBakeOutput;
        }

        internal string KeyFor(Module module) {
            return cachedKey ?? (cachedKey = module.PrefKey + "." + Suffix);
        }
    }

    /**
     * A per-module semicolon-separated wildcard-list setting (see Globs), stored at
     * com.furyplusplus.module.&lt;moduleId&gt;.&lt;suffix&gt;. List settings exist to change bake
     * output, so they always feed the bake-cache config key (normalized).
     */
    internal sealed class ModuleListOption {
        internal readonly string Suffix;
        internal readonly string Label;
        internal readonly string Description;

        private string cachedKey;

        internal ModuleListOption(string suffix, string label, string description = "") {
            Suffix = suffix;
            Label = label;
            Description = description;
        }

        internal string KeyFor(Module module) {
            return cachedKey ?? (cachedKey = module.PrefKey + "." + Suffix);
        }
    }
}
