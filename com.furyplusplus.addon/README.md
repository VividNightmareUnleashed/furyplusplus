# FuryPlusPlus

FuryPlusPlus is an Editor-only, bolt-on layer for an existing VRCFury installation, covering both
**bake speed** and (in upcoming releases) **output quality** — fewer animator layers, fewer synced
parameter bits. It profiles VRCFury's bake and replaces measured hot paths with indexed
implementations. It does not ship, fork, or modify VRCFury.

FuryPlusPlus is the successor to **QuickFury** and includes ports of all 21 of its validated speed
patches. The two cannot run together: while QuickFury is installed, FuryPlusPlus disables
QuickFury's patches each session and warns. Remove `com.quickfury.addon` (settings do not carry
over).

FuryPlusPlus 0.1.0 is tested against VRCFury 1.1363.0. On the reference avatar it reduced a warm
VRCFury bake from **93.8 seconds (stock) to 12.6 seconds**, timing-equivalent to QuickFury 1.2.4
on the same avatar (12.0 s, within run-to-run variance).

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

The package's VPM dependency is a minimum needed for installation resolution; it does not mean
later VRCFury versions are compatible with the version-pinned modules.

## Install

1. Install VRCFury normally and confirm the avatar builds without FuryPlusPlus.
2. If QuickFury is installed, remove it. (FuryPlusPlus suppresses QuickFury's patches while it is
   present, but the package should not stay installed.)
3. In Unity, choose **Window > Package Manager**, use **+ > Add package from disk**, and select
   this package's `package.json`.
4. Wait for the Editor to recompile. The Console should report
   `[FuryPlusPlus] Ready: 19/19 modules installed for VRCFury 1.1363.0`.

For a local file dependency, the equivalent `Packages/manifest.json` entry is:

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
