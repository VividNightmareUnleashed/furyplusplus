# Updating Fury++ from Unity

Fury++ can check for stable releases and install them from its settings window.
Automatic checks require a separate opt-in from VRCFury compatibility checks.
Every installation requires confirmation, even after automatic checks are enabled.

## Checks and consent

The first prompt explains the request to GitHub Pages and offers **Allow update
checks** and **No thanks**. Either choice is remembered for the current computer's
user. Checks run at Editor startup and once a day while the Editor stays open;
script reloads reuse the session's last result. Disabling Fury++ or its **Fury++
updates** module pauses automatic checks. Batch mode performs no update checks.

In **Tools > FuryPlusPlus > Settings**, use **Automatically check for Fury++
updates** to change the choice. **Check for updates** makes a one-time request
without enabling automatic checks. When a newer version is found, **Release
notes** opens its GitHub release and **Install version** asks for confirmation.

Checks download the public VPM listing over HTTPS. GitHub receives the client's
IP address, but Fury++ sends no avatar data or installed version information.
Confirming installation also authorizes downloading the compatibility list and
release archive. Compatibility approvals saved during this process do not enable
automatic compatibility checks.

## Which installations can update

The updater supports ordinary packages in `Packages/com.furyplusplus.addon`,
including VCC installs and packages copied or imported there manually. It checks
that the published release has the same Unity and dependency requirements as the
installed package. It also requires a current approval for the new Fury++ version
with the installed VRCFury version. VRCFury and the VRChat SDK are not updated.

Use Creator Companion when requirements change, another VPM package depends on
Fury++, or the VPM record differs from the installed package. Linked packages,
junctions, Git checkouts and Unity Package Manager dependencies must be updated
through their original installation source. The updater explains these cases
without replacing package files.

## Installation and recovery

The updater accepts stable three-part versions from the Fury++ VPM listing and
only the expected GitHub release asset URL. It verifies the published SHA-256,
package name, version and requirements before replacing files. Archive extraction
rejects paths outside its staging directory, duplicate paths, links and special
files. Downloads are bounded to 1 MiB for the listing, 256 KiB for approvals and
16 MiB for the archive; extraction is limited to 64 MiB and 4,096 entries.

Installation waits until Unity is outside play mode, compilation, asset import
and detected builds or SDK uploads. Save modified scenes when prompted. Downloads that finish
while Unity is busy require a retry. Script reloads and asset editing are locked
during package replacement. The old directory is moved to a backup so obsolete
files do not survive in the new package. For VCC installs, only Fury++'s direct
and locked version records are updated. If the file transaction fails, the old
package is restored. No installer program or shell command is downloaded or run.

Backups are retained in `Library/FuryPlusPlus/Updates/<id>/`. The Console records
the backup path after installation. Each successful update retains
`previous-package/` and, for VPM-managed installs, the previous
`vpm-manifest.json`. Backups use disk space and are lost if `Library` is deleted.

If Unity is interrupted during replacement, close Unity and restore
`previous-package/` to `Packages/com.furyplusplus.addon`, preserving any current
package folder elsewhere first. Restore the backed-up VPM manifest only if no
other package changes have been made since that backup. Then reopen Unity.

An installation can still have compile or runtime problems despite valid files.
The backup covers Fury++ and its VPM record, not the whole project. Live Editor
installation, script reload, consent dialogs and avatar bake validation remain
part of release testing.
