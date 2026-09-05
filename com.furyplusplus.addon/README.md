# FuryPlusPlus

FuryPlusPlus is an Editor-only, bolt-on layer for an existing VRCFury installation, covering both
**bake speed** and **output quality** — fewer animator layers, fewer synced parameter bits. It
profiles VRCFury's bake, replaces measured hot paths with indexed implementations, and adds
conservative post-build passes that shrink the baked result. It does not ship, fork, or modify
VRCFury.

FuryPlusPlus is the successor to **QuickFury**. Its original speed patches have been adapted or
retired as VRCFury incorporates equivalent optimizations. The two cannot run together:
while QuickFury is installed, FuryPlusPlus disables
QuickFury's patches each session and warns. Remove `com.quickfury.addon` (settings do not carry
over).

FuryPlusPlus 1.2.5 targets VRCFury 1.1427.0. Historical measurements with FuryPlusPlus 1.2.0
and VRCFury 1.1382.0 reduced a reference-avatar bake from **about 33 seconds to 3.7 seconds**,
with most remaining savings coming from Armature Link. These are not measurements of the
current release; gains depend on the avatar and enabled modules. Version 1.2.5 passed 129 Unity
EditMode tests and stock/optimized avatar bake comparisons with VRCFury 1.1427.0.

## Requirements and compatibility

- Unity 2022.3
- VRChat Avatars SDK 3.10.3 or newer
- VRCFury 1.1427.0 or newer within 1.x, installed separately
- An approved combination of the exact FuryPlusPlus and VRCFury versions

FuryPlusPlus checks a public approval list on GitHub. New VRCFury versions can
be approved for an existing FuryPlusPlus release after review and testing,
without requiring another package release just to update a version pin.
Each module still checks its required methods and fields when it installs.

Unknown combinations run stock VRCFury with optimization modules disabled;
profiling and editor visuals remain eligible. The package's VPM dependency range
allows newer VRCFury versions to be installed, but does not approve them.

FuryPlusPlus asks permission before checking GitHub automatically at Editor startup
and after script reloads. You can decline or change your choice in settings. No avatar
data or installed version information is sent; GitHub receives a normal web request,
including your IP address. A cached list remains usable
for 30 days, so temporary connection failures do not immediately disable a
working setup. On a first install, check approvals in FuryPlusPlus settings,
then restart Unity or reload scripts after an approval is downloaded. Downloaded
changes always wait for a script reload; they never change patches during a bake.
The settings window shows the current decision and a **Check once on GitHub**
button that does not enable automatic checks. The tested 1.2.5/1.1427.0 approval has
been prepared locally and becomes available after the catalog is published.

## Fury++ updates

In **Tools > FuryPlusPlus > Settings**, opt in to automatic Fury++ release checks
or use **Check for updates** once. Automatic checks contact GitHub Pages at Editor
startup and daily; this consent is separate from VRCFury compatibility checks.
Every installation asks for confirmation, verifies the release checksum and
compatibility approval, and keeps a backup of the package and its VPM record.

The updater supports ordinary packages inside the project's `Packages` folder.
Updates with changed Unity or dependency requirements use Creator Companion.
Linked packages and Git checkouts use their original installation source.
VRCFury and the VRChat SDK are not updated. See the
[update and recovery guide](https://github.com/VividNightmareUnleashed/furyplusplus/blob/master/UPDATES.md).

## Install

1. Install VRCFury normally and confirm the avatar builds without FuryPlusPlus.
2. If QuickFury is installed, remove it. (FuryPlusPlus suppresses QuickFury's patches while it is
   present, but the package should not stay installed.)
3. Add the package through the Creator Companion or by hand — both methods below.
4. Check that the installed combination is approved in FuryPlusPlus settings. Once approved and reloaded, the Console should report
   `[FuryPlusPlus] Ready: 30/30 modules installed for VRCFury 1.1427.0, 15 superseded`.

### Via the VRChat Creator Companion (recommended)

Open the [FuryPlusPlus package listing](https://vividnightmareunleashed.github.io/furyplusplus/)
and press **Add to VCC**, or paste the listing URL under **Settings > Packages > Add Repository**:

```text
https://vividnightmareunleashed.github.io/furyplusplus/index.json
```

Then open your avatar project's **Manage Project** page and add **FuryPlusPlus**. Updates arrive
through the Creator Companion like any other package.

### From a release zip or local clone

In Unity, choose **Window > Package Manager**, use **+ > Add package from disk**, and select this
package's `package.json`. For a local file dependency, the equivalent `Packages/manifest.json`
entry is:

```json
"com.furyplusplus.addon": "file:C:/path/to/furyplusplus/com.furyplusplus.addon"
```

Keep FuryPlusPlus as its own package. Do not copy files into the VRCFury package, which would make
upgrades and rollback harder.

## Use

Open the FuryPlusPlus window via **Tools > FuryPlusPlus > Settings…**. Every module has its own
kill switch, grouped by category; settings are stored in Unity `EditorPrefs`, so they apply to the
current Editor user rather than being serialized into the avatar. **Restore recommended** resets
the module defaults; **Disable all optimizations**
gives an immediate stock-VRCFury control run.

### Active build speed modules

- **Armature constraint / PhysBone / skin / destroy indexes**: replace Armature Link's thousands
  of whole-avatar scans with per-phase indexes.
- **Armature debug-component suppression**: avoids creating diagnostic components on merged
  bones during preview builds; upload output is unchanged.
- **Covered SPS mesh probe skip** and **SPS material probe cache**.
- **Compressor memoization**: reuses repeated parameter and menu queries within a bake.
- **Fast blendshape optimizer bake**: one-pass rewrite of Blendshape Optimizer's bake step, with
  a default-on fix for VRCFury's multi-frame interpolation frame selection (stock behavior
  selectable per sub-toggle).
- **Play-mode pass skipping**: passes that only matter for uploads (mipmap streaming fix, menu
  icon textures, final validation) are skipped during play-mode test builds.

Fifteen older speed modules are retired because VRCFury now handles their work natively.
Their settings remain visible, struck through and linked to the upstream replacement.

### Output quality modules (change bake output)

- **Unused synced-parameter stripper**: un-syncs (never deletes) synced expression parameters no
  controller reads, with keep-list globs and a keep-dynamics option.
- **Int-to-Bool narrowing**: synced Ints whose entire observable usage is 0/1 become Bools.
- **Compressor family**: trailing-bool lane packing, an exhaustive batch solver, user-listed
  eligibility additions, and optional sub-8-bit packing of paired floats. Desktop/mobile
  alignment is guarded by a build sidecar that fails divergent mobile builds.
- **Full-scope DBT**, **no-op curve stripping**, **clip dedup**, **off-side elimination**, and
  **DBT layer consolidation**: fewer FX layers and smaller controllers through VRCFury's own
  optimizer plus conservative post-passes.
- **Toggle conversions** (default off): "Separate Local State" layers and pure-crossfade toggles
  become blendtree branches; see the module descriptions for the documented feel deltas.

### Play-mode iteration (experimental, default off)

- **Bake cache + replay**: fingerprints every play-mode bake; on an exact match the whole
  NDMF+VRCFury preprocessor chain is skipped and the avatar restores from a snapshot in well
  under a second. Play entry becomes meaningfully faster and far lighter on CPU/memory/disk.
  Uploads are never cached.
- **No-disk-save**: skips VRCFury's end-of-bake disk serialization during play-mode test builds.
- **Dry-run telemetry**: logs would-have-hit verdicts and potential savings without replaying.

### Profiling

FuryPlusPlus always records total bake time and exact VRCFury action durations when compatible
profiling targets are present. Enable **Detailed profiling** in the window for method-level
inclusive/self time and call counts (installs on the spot, sheds on the next script reload). Use
**Log last profile report** to print the most recent report again.

The public `FuryPlusPlus.FuryPlusPlusProfilerApi.LastReport` property exposes the current
in-memory report; it is also stored in the Editor session under `FuryPlusPlus.LastProfile`.

## Safety and rollback

FuryPlusPlus patches Editor methods at assembly load and removes only patches registered under its
own Harmony ID before reload. It never changes VRCFury package files. To roll back, disable the
toggles, remove the FuryPlusPlus package dependency, and let Unity recompile.

**FuryPlusPlus is an unofficial third-party addon. It is not supported, endorsed, or maintained by
VRCFury, and no guarantee is made that it will work correctly for every avatar, project, or future
release. Do not report a problem to VRCFury while FuryPlusPlus is installed. Remove FuryPlusPlus
completely, let Unity recompile, and reproduce the issue with stock VRCFury first. Problems that
occur only with FuryPlusPlus installed belong in the FuryPlusPlus issue tracker. FuryPlusPlus is
provided without warranty and is used at your own risk.**

Treat any VRCFury upgrade as unsupported until FuryPlusPlus is re-profiled and revalidated against
that exact release. Unapproved combinations retain profiling but fail closed for every version-gated
module. See [NOTICE.md](NOTICE.md) for the VRCFury commercial-license considerations that apply to
all VRCFury-patching tools.
