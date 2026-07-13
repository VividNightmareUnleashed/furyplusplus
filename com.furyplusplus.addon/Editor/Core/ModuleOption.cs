namespace FuryPlusPlus {
    /** A per-module sub-toggle, stored at com.furyplusplus.module.&lt;moduleId&gt;.&lt;suffix&gt;. */
    internal sealed class ModuleOption {
        internal readonly string Suffix;
        internal readonly string Label;
        internal readonly bool Default;
        internal readonly string Description;

        internal ModuleOption(string suffix, string label, bool defaultValue, string description = "") {
            Suffix = suffix;
            Label = label;
            Default = defaultValue;
            Description = description;
        }
    }
}
