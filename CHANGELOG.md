# Changelog

## 1.0.1 — 2026-07-14

Distribution and metadata release; bake behavior is unchanged from 1.0.0. FuryPlusPlus is now
installable through the VRChat Creator Companion, and the manifest pins VRCFury to the exact
validated version so the Companion only installs and keeps combinations that actually work.

- **Creator Companion support:** a hosted VPM listing at
  https://vividnightmareunleashed.github.io/furyplusplus/ with an Add to VCC button; releases
  now ship a VPM zip alongside the unitypackage, and the listing rebuilds automatically on every
  release.
- **Exact VRCFury pin:** the VPM dependency on `com.vrcfury.vrcfury` is now exactly `1.1363.0`
  (was `>=`), so the Companion refuses to update VRCFury past the validated version while
  FuryPlusPlus is installed. From-disk installs still load any version and fail closed as
  before.
- **Settings-window footer:** the window now shows the addon version and author credit, linking
  to the GitHub page.
- **License metadata:** the license is declared in the package manifest, and LICENSE.md,
  NOTICE.md, and the README are mirrored at the repository root.

## 1.0.0 — 2026-07-14

First full release: the output-quality passes join the ported speed patches, so FuryPlusPlus now
covers both halves of its charter — faster bakes and leaner bake output. On the reference avatar
the quality passes cut synced parameter data from 444 to 177 bits, and the new speed modules bring
the warm bake from 12.6 s at 0.1.0 down to roughly 11–12 s (93.8 s stock). 44 modules total.

### Output quality (change bake output)

- **Unused synced-parameter stripper:** post-build pass that un-syncs (never deletes) synced
  expression parameters no controller reads. Keep-list globs and a keep-dynamics option; refuses
  to touch parameter assets VRCFury did not generate.
- **Int-to-Bool narrowing:** synced Ints whose entire observable usage is 0/1 become Bools
  (7 bits each). Closed-world eligibility; OSC-suspect parameters are skipped and reported.
- **Parameter compressor family:** trailing-bool lane packing, an exhaustive batch solver that
  replaces the greedy one (444 → 177 bits at 3 batches per sync on the reference avatar),
  user-listed eligibility additions, and optional sub-8-bit packing of paired floats. Desktop
  decisions replay onto mobile through VRCFury's own alignment file, and a build sidecar
  hard-fails mobile builds whose inputs diverge from the desktop upload.
- **Full-scope DBT:** injects DirectTreeOptimizer on the build clone so hand-authored FX layers
  are eligible for VRCFury's own layer-to-tree conversion, with its per-layer guards intact.
- **No-op curve stripping, controller-wide clip dedup, off-side elimination, DBT layer
  consolidation:** fewer FX layers and smaller controllers via conservative passes that only act
  when every writer/binding check proves the change unobservable.
- **Toggle conversions (default off):** "Separate Local State" 3-state toggle layers become an
  IsLocal-selected blendtree branch, and pure-crossfade toggles become a smoothed-parameter fade
  tree (documented feel deltas; off until judged in-game).

### Build speed

- New memo/index modules beyond the QuickFury ports: layer-to-tree binding index, compressor
  menu-walk memoization, `VFController.GetLayers` cache, Full Controller merge path-validation
  memo, and a motion-graph traversal cache with shadow validation.
- **Fast blendshape optimizer bake:** one-pass rewrite of the Blendshape Optimizer bake (4.3×
  on that phase) with a default-on fix for VRCFury's multi-frame interpolation frame selection
  (stock-identical behavior selectable per sub-toggle).
- **Play-mode pass skipping:** upload-only passes (mipmap streaming fix, menu icon textures,
  final validation) are skipped during play-mode test builds only.

### Play-mode iteration (experimental, default off)

- **Bake cache replay:** on a whole-chain fingerprint HIT the entire NDMF+VRCFury preprocessor
  chain is skipped and the avatar is restored in place from a cached snapshot of the previous
  processed result. The restore takes well under a second; total play entry is meaningfully
  faster but still dominated by Unity's normal scene/avatar startup. The bigger win is resource
  load: the CPU-heavy bake never runs, so entering play mode is far lighter on CPU/memory/disk —
  most noticeable on weaker PCs or while working in VR. Snapshots live in
  `Packages/com.furyplusplus.bakecache`, are replaced atomically after each successful bake, and
  are validated against the sidecar's fingerprint hashes before every replay. Play-mode only;
  uploads are never cached. Ships with a "capture snapshots but never replay" validation option
  and a Clear-bake-cache button. Known limits: avatars referencing scene objects outside their
  own hierarchy are not cached; AudioLink's play-mode refresh is skipped on replayed bakes. The
  cache key covers output-relevant modules and options only, so cosmetic or pure-speed toggles
  do not invalidate snapshots.
- **No-disk-save:** skips VRCFury's end-of-bake disk serialization during play-mode test builds
  (~3,300 disk writes and 1.5–3 s per play entry on the reference avatar). Never active for
  uploads; a domain reload while playing loses the in-memory bake.
- **Bake-cache dry-run telemetry:** fingerprints each play-mode bake and logs whether a cache
  would have hit — and how much time it would have saved — without replaying anything.

### UI and core

- Settings window redesign: stat-card header (sync-bit gauge, FX layer count, last bake time),
  category tabs, per-module chips that only ever show measured or projected numbers, and a
  one-shot Benchmark that records a stock-VRCFury baseline and overlays a per-phase comparison
  breakdown. Baseline persists per project in `UserSettings/`.
- Liquid progress bar with a repaint pump so long phases keep animating; accent theme, status
  badge, and degraded-mode warning banner on VRCFury's progress window; native progress bars
  during bake-cache capture and replay.
- Welcome window with read-only per-avatar projections (synced bits vs cap, strippable
  parameters, narrowable Ints, FX layer count, non-animated blendshapes); opens once per project
  and shares the estimators' logic with the real passes so numbers cannot drift.
- QuickFury coexistence: instead of refusing to initialize, FuryPlusPlus now suppresses
  QuickFury's patches (console warning each reload, dialog once per session) and runs normally.
- An untested VRCFury version now raises a modal warning once per session instead of a passive
  console line; experimental modules are labeled ⚗️EXPERIMENTAL in the window.
- Internal consolidation: shared compat holders resolve each reflected member once, typed stats
  replace string parsing in the window, and estimator projections call the passes' own
  classification predicates.

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
