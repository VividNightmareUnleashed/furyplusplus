# Changelog

## 1.2.5 — 2026-09-05

Adds compatibility approvals for exact FuryPlusPlus/VRCFury release pairs. New VRCFury
releases can be approved after testing without rebuilding FuryPlusPlus solely to change a pin.

- **GitHub approval list:** unknown combinations keep optimizations disabled. Each approved
  module still checks its required methods and fields when it installs.
- **Optional automatic checks:** a one-time prompt asks permission and explains what is
  sent to GitHub. Settings remember the choice and offer a separate one-time check.
- **Fury++ updater:** separate consent enables release checks at Editor startup and daily.
  Each installation asks for confirmation, verifies the release checksum and VRCFury
  approval, and keeps a backup. Updates with changed requirements use Creator Companion.
- **Armature pruning:** commits batched skin updates before VRCFury scans bone usage,
  so obsolete outfit bones are removed as they are in stock VRCFury.
- **Optimization guards:** build-phase callbacks honor both switches. Blendtree merging
  preserves intervening layer priority; toggle conversions retain masks and altered
  playback by leaving those layers alone. No-op stripping preserves additive writers.
- **Older edge cases:** dynamics outputs cannot be narrowed to booleans. Clip keys
  distinguish escaped strings and additive poses; SPS caches include the VRCFury
  implementation. The blendshape fix corrects below-first-frame scaling as well.
- **Saved compression data:** rejects missing parameter lists, non-boolean compression
  flags and duplicate fields instead of accepting uncertain desktop/mobile layouts.
- **Cached decisions:** background checks update a project-local cache, valid for 30 days.
  Downloaded changes apply after a script reload, never during a bake. Settings show the
  current decision and offer a manual approval check.
- **VRCFury dependency range:** permits 1.1427.0 through later 1.x releases to be installed;
  installation alone does not approve a combination. The prepared catalog approves
  1.2.5 with VRCFury 1.1427.0 after Unity tests and avatar bake comparisons.
- **Current feature documentation:** both READMEs now distinguish active optimizations from
  retired modules and label older performance measurements with their tested version.

## 1.2.4 — 2026-08-24

Tracks VRCFury 1.1426 and tightens FuryPlusPlus's compatibility boundaries. VRCFury 1.1417-1.1426
reworked its SPS shader patcher, moved the write-defaults decision into its own build phase, and
switched many error messages to `GetDebugPath`; the pinned members FuryPlusPlus consumes remain
compatible. Bake output is unchanged.

- **VRCFury pin → 1.1426.0:** the exact package and runtime compatibility pins move from
  1.1416.0 to 1.1426.0.
- **Centralized compatibility surface:** reflection and dynamic construction now live behind
  subsystem-specific accessors in `Editor/Compat/`, with missing members failing closed.
- **Safer cross-platform verification:** sidecars use structured JSON, reject invalid blueprint
  IDs, and hard-fail unreadable or uncertain desktop/mobile layout data.
- **Lower hot-path overhead:** clip deduplication and SPS probing latch settings at phase
  boundaries; package identity and large bake orchestration flows have single owners.

## 1.2.3 — 2026-08-10

Tracks VRCFury 1.1416. VRCFury added an optional debug-path flag to
`VFGameObject.GetPath`.

- **VRCFury pin → 1.1416.0:** the exact package and runtime compatibility pins move from
  1.1408.0 to 1.1416.0.
- **Kept the fast blendshape optimizer active:** its reflected path lookup now binds the
  three-argument `GetPath(root, prettyRoot, removeCloneFromRoot)` signature and passes
  `false` for the new flag, preserving the previous bake-log path behavior.

## 1.2.2 — 2026-08-03

Tracks VRCFury 1.1408. VRCFury now builds the parameter compressor in the Action controller
and passes that controller directly to `ParameterCompressorLayerService.BuildLayer`.

- **VRCFury pin → 1.1408.0:** the exact package and runtime compatibility pins move from
  1.1394.0 to 1.1408.0.
- **Ported opt-in 4-bit float pairs to the Action compressor:** sub-8 layer surgery now uses
  the controller passed to `BuildLayer(decision, controller)`, keeping its encode, decode and
  compressor layers together without depending on the removed controller-service field or an
  FX lookup.

## 1.2.1 — 2026-07-27

Tracks VRCFury 1.1394. VRCFury now keeps SaveAssets inside its build-wide asset-editing
scope, requires an explicit avatar root when validating animation bindings, and commits a
save session through `SaveAssetsSession.Finish()`.

- **VRCFury pin → 1.1394.0:** the exact package and runtime compatibility pins move from
  1.1382.0 to 1.1394.0.
- **SaveAssets batching retired as native:** VRCFury's save rewrite no longer exits and
  re-enters asset editing during SaveAssets. The old Unity 2022 patch has been removed and
  its toggle remains struck through with the upstream replacement linked.
- **Fixed the separate-local and fade toggle converters on 1.1394:** both validators now
  pass the same avatar binding root as VRCFury's own layer-to-tree optimizer.
- **Updated experimental play-mode no-disk saving:** the patch now suppresses the
  `SaveAssetsSession.Finish()` commit and scopes the controller-list save overload, covering
  both the main save and the parameter compressor's late FX-only re-save.

## 1.2.0 — 2026-07-24

Tracks VRCFury 1.1382. VRCFury 1.1370–1.1382 replaced its animation model wholesale: clips,
bindings, motions, layers and state machines are now detached in-memory objects, loaded once,
edited, and written back at the end, and several of the extension classes FuryPlusPlus patched
were deleted along the way. Every module was revalidated against the new model — most were
rewritten onto it, and twelve are retired because VRCFury now does the same work natively.
Nothing is left disabled. Where a module claims output-identical behavior, that still holds:
skin bone arrays and full blendshape and mesh state hash identically with the speed modules on
and off.

Worth setting expectations: VRCFury's own rewrite made stock bakes roughly 2.4× faster on the
test avatar. FuryPlusPlus still takes a ~33s bake down to ~3.7s there, but the gap is narrower
than it was, and most of what remains comes from Armature Link — avatars that don't lean on it
will see less.

- **VRCFury pin → 1.1382.0:** the exact `com.vrcfury.vrcfury` pin moves from 1.1367.0 to
  1.1382.0, so the Creator Companion installs and keeps this version. From-disk installs still
  load any version and fail closed as before.
- **Twelve more modules retired as native:** "Ordered path rewrite", "Fast Armature Link
  moves", "Blendshape binding cache", "Layer-to-tree binding index", "Controller parameter
  index", "Behaviour container filter", "Tracking behaviour index", "Full Controller merge path
  cache", "Motion graph traversal cache", "Fast SaveAssets discovery", "Consolidated asset
  container" and "Fast controller asset graph". VRCFury now caches or restructures the same
  work itself, so keeping ours would only stack a second mechanism with a different
  invalidation point. Their toggles stay in the settings window, struck through and linking the
  VRCFury commit that absorbed them.
- **The whole asset-saving group is now native.** VRCFury narrowed its end-of-build asset scan
  from every component on the avatar down to the avatar, Renderers, MeshFilters and
  AudioSources, stopped scanning the same component twice, added a single shared "VRCFury
  Other" container for generated assets instead of a file each, and stopped re-walking
  controllers to rediscover assets it had already collected. That is the entire premise of
  those three modules. "Fast SaveAssets discovery" also loses its "overrides VRCFury" note —
  the pass it used to override has itself been replaced.
- **Two retirements were decided by measurement, not by a changed signature:** the motion-graph
  walks "Motion graph traversal cache" existed to cache now measure 79ms of a 3.7s bake, and
  binding validation no longer searches the hierarchy at all, so "Full Controller merge path
  cache" has nothing left to cache.
- **Fixed: SPS material probe cache cost ~3s on an unrelated phase.** VRCFury now holds one
  asset-editing batch open for the whole build, and hashing asset dependencies inside it
  deferred import work onto the next material write. The cache now prefills from the avatar
  before the batch opens and treats a miss as "don't cache".
- **Fixed: one-sided blendtree conversion could empty the wrong side.** The conversion assumed
  the off side is always the second motion, but VRCFury swaps the two for `IfNot`, `Less` and
  `NotEqual` conditions — on those, the toggle's content could have been emptied instead of its
  off state. It now picks the side that survives that normalization.
- **Faster Blendshape Optimizer bake:** the rewritten bake avoids re-reading and rewriting whole
  mesh vertex arrays per shape, taking that phase from ~463ms to ~70ms on the test avatar with
  bit-identical output.
- **Clip deduplication moved to save time:** identical generated clips now hand back the first
  one's asset as it is written, instead of being created and then repointed. This also absorbs
  the deduplication half of the retired "Fast controller asset graph". 325 duplicates collapsed
  on the test avatar.

## 1.1.2 — 2026-07-15

Tracks VRCFury 1.1367. VRCFury 1.1366 and 1.1367 were haptics and in-editor rendering fixes — an
OGB fix for avatars with unusual root scales, and a reworked scene-view SPS greenscreen fix — none
of which touch the build-speed or bake-output paths FuryPlusPlus optimizes. This is a pin bump
only: no module changes, and baked output is unchanged.

- **VRCFury pin → 1.1367.0:** the exact `com.vrcfury.vrcfury` pin moves from 1.1365.0 to 1.1367.0,
  so the Creator Companion installs and keeps this version. From-disk installs still load any
  version and fail closed as before.

## 1.1.1 — 2026-07-15

Log-cosmetic fix, no behavior or bake-output change from 1.1.0. The bootstrap console line
counted the two modules superseded by VRCFury 1.1365 in its "modules installed" total, so a
clean install read `Ready: 42/44` — as if two modules had failed. Superseded modules now leave
that ratio (it reads `42/42`) and are noted separately.

## 1.1.0 — 2026-07-15

Tracks VRCFury 1.1365. VRCFury 1.1364 added its own build-speed caching that overlaps a
couple of FuryPlusPlus modules, so this release re-pins to the new validated version and
reconciles the overlap: where VRCFury now matches us we step aside, and where we still win we
stay and say so. Baked output is unchanged — byte-identical to stock VRCFury — and the ~7–10×
speedup on a heavy avatar holds.

- **VRCFury pin → 1.1365.0:** the exact `com.vrcfury.vrcfury` pin moves from 1.1363.0 to
  1.1365.0, so the Creator Companion installs and keeps this version. From-disk installs still
  load any version and fail closed as before.
- **Dropped two now-native modules:** VRCFury 1.1364 gave VFController a native cache — the
  layer list plus a state-machine-to-layer-id lookup — that does the same work as the
  "Controller layer-list cache" and "Layer-to-tree layer index" modules. Benchmarked, VRCFury's
  version keeps pace, so keeping ours would only stack a second cache with a different
  invalidation point. Both are removed; their toggles stay in the settings window, struck
  through and linking the VRCFury commit that absorbed them.
- **"Fast SaveAssets discovery" now marked as overriding VRCFury:** VRCFury 1.1364 also added
  its own SaveAssets de-duplication, but the discovery pass here still benchmarks faster and
  bypasses it, so the module stays on and its row shows an "overrides VRCFury" note linking the
  upstream commit.

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
