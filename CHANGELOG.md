# Changelog

## Unreleased

- **Bake cache replay (experimental, default off):** on a whole-chain fingerprint HIT the entire
  NDMF+VRCFury preprocessor chain is skipped and the avatar is restored in place from a cached
  snapshot of the previous processed result. The restore takes well under a second; total play
  entry is meaningfully faster but still dominated by Unity's normal scene/avatar startup. The
  bigger win is resource load: the CPU-heavy bake never runs, so entering play mode is far
  lighter on CPU/memory/disk — most noticeable on weaker PCs or while working in VR.
  Snapshots (processed-avatar prefab + deep copies of every transient dependency) live in
  `Packages/com.furyplusplus.bakecache`, are replaced atomically after each successful bake, and
  are validated against the sidecar's fingerprint hashes before every replay. Play-mode only;
  uploads are never cached. Ships with a "capture snapshots but never replay" validation option
  and a Clear-bake-cache button. Known limits: avatars referencing scene objects outside their own
  hierarchy are not cached; AudioLink's play-mode refresh is skipped on replayed bakes.
- Bake-cache config hash now includes per-module sub-option states (`DescribeStates`), so flipping
  a sub-toggle invalidates the cache. One-time effect: all fingerprint records from earlier builds
  log a config-hash MISS on their first bake after upgrading.

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
