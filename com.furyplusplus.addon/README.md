# FuryPlusPlus

FuryPlusPlus is an Editor-only, bolt-on layer for an existing VRCFury installation, covering both
**bake speed** and **output quality** — fewer animator layers, fewer synced parameter bits. It
profiles VRCFury's bake, replaces measured hot paths with indexed implementations, and adds
conservative post-build passes that shrink the baked result. It does not ship, fork, or modify
VRCFury.

FuryPlusPlus is the successor to **QuickFury** and includes ports of all 21 of its validated speed
patches. The two cannot run together: while QuickFury is installed, FuryPlusPlus disables
QuickFury's patches each session and warns. Remove `com.quickfury.addon` (settings do not carry
over).

FuryPlusPlus 1.0.1 is tested against VRCFury 1.1363.0. On the reference avatar it reduced a warm
VRCFury bake from **93.8 seconds (stock) to 11–12 seconds**, and its output-quality passes cut
the avatar's synced parameter data from **444 to 177 bits**.

## Requirements and compatibility

- Unity 2022.3
- VRChat Avatars SDK 3.10.3 or newer
- VRCFury installed separately
- Behavior-changing modules: **VRCFury 1.1363.0 exactly**

FuryPlusPlus discovers VRCFury's internal Editor methods at load time. Profiling remains available
whenever the profiling signatures match, but version-pinned modules are disabled unless the
installed VRCFury version is exactly `1.1363.0`. Each module also checks its own target signatures
and stays disabled if they differ. This is deliberately fail-closed because VRCFury does not expose
a public extension API for these bake internals.

The package's VPM dependency pins VRCFury to the exact validated version, so the Creator
Companion only installs and keeps the combination the version-pinned modules support — it will
refuse to update VRCFury past the pin while FuryPlusPlus is installed. A from-disk install skips
that resolution; any VRCFury version loads, and unsupported versions simply run with every
version-pinned module disabled.

## Install

1. Install VRCFury normally and confirm the avatar builds without FuryPlusPlus.
2. If QuickFury is installed, remove it. (FuryPlusPlus suppresses QuickFury's patches while it is
   present, but the package should not stay installed.)
3. Add the package through the Creator Companion or by hand — both methods below.
4. Wait for the Editor to recompile. The Console should report
   `[FuryPlusPlus] Ready: 44/44 modules installed for VRCFury 1.1363.0`.

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
current Editor user rather than being serialized into the avatar. All measured, parity-checked
modules default on; **Restore recommended** returns to that set, **Disable all optimizations**
gives an immediate stock-VRCFury control run.

### Build speed modules (output-identical, ported from QuickFury)

- **Armature constraint / PhysBone / skin / destroy indexes**: replace Armature Link's thousands
  of whole-avatar scans with per-phase indexes.
- **Fast Armature Link moves** and **Armature debug-component suppression**.
- **Ordered path rewrite** (+ *skip empty deferred rewrites*): ordered prefix index with
  chronological rewrite semantics preserved.
- **Layer-to-tree layer index** and **controller parameter index**: O(1) lookups where VRCFury
  scans arrays.
- **Fast SaveAssets discovery**, **SaveAssets batching (Unity 2022)**, **consolidated asset
  container**, **fast controller asset graph** (+ *deduplicate generated clips*), **blendshape
  binding cache**.
- **Covered SPS mesh probe skip** and **SPS material probe cache**.
- **Tracking behaviour index** and **behaviour container filter**.
- Two conservative SaveAssets scan-skips remain experimental and default off.

### Additional build speed modules

- **Layer-to-tree binding index**, **compressor memoization**, **GetLayers cache**, **Full
  Controller merge path memo**, and a **motion-graph traversal cache** (shadow-validated):
  further measured hot-path replacements beyond the QuickFury ports.
- **Fast blendshape optimizer bake**: one-pass rewrite of Blendshape Optimizer's bake step, with
  a default-on fix for VRCFury's multi-frame interpolation frame selection (stock behavior
  selectable per sub-toggle).
- **Play-mode pass skipping**: passes that only matter for uploads (mipmap streaming fix, menu
  icon textures, final validation) are skipped during play-mode test builds.

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
that exact release. Unknown versions retain profiling but fail closed for every version-pinned
module. See [NOTICE.md](NOTICE.md) for the VRCFury commercial-license considerations that apply to
all VRCFury-patching tools.
