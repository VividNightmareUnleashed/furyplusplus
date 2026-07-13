# Changelog

## 0.1.0 — 2026-07-13

Initial release: full speed parity. FuryPlusPlus supersedes QuickFury with a fresh module
framework sized for the upcoming output-quality modules, and ships ports of all 21 QuickFury speed
patches (byte-for-byte patch bodies, scripted-diff verified against QuickFury 1.2.4).

- Module framework: explicit ordered registry, per-module kill switches (EditorPrefs), fail-closed
  installs, three compat tiers (Profiling on any VRCFury / PublicSdk / ExactVersion pinned to
  VRCFury 1.1363.0).
- Dedicated FuryPlusPlus window (Tools > FuryPlusPlus > Settings…) with per-category module groups,
  master switch, restore/panic buttons, and profiling controls.
- Always-on bake profiler with opt-in detailed tier and per-module stats footer; public
  `FuryPlusPlusProfilerApi.LastReport`.
- BuildPhaseHooks: register callbacks before/after any named VRCFury FeatureOrder phase
  (foundation for the quality modules).
- QuickFury coexistence refusal: FuryPlusPlus disables itself entirely while `com.quickfury.addon`
  is present.
- Verification on the reference avatar: warm bake 93.8 s stock → 12.6 s (86.5% faster);
  timing-equivalent to QuickFury 1.2.4 (12.0 s) with zero patch fallbacks; 25/25 EditMode tests.
