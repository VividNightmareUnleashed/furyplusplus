# Approving VRCFury releases

FuryPlusPlus reads `compatibility.json` from this repository's default branch
over HTTPS. Each entry approves one exact FuryPlusPlus release with explicitly
listed VRCFury releases. Approval does not replace the module's checks for its
required methods and fields.

The prepared catalog approves FuryPlusPlus 1.3.0 with VRCFury 1.1427.0 after
129 passing EditMode tests and a successful avatar bake under the new version.
Testing used Unity 2022.3.22f1 and VRChat SDK 3.10.4. Captured output matched the
earlier 1.2.5 candidate: hierarchy, controller parameters, 456 clips and 30 skinned
meshes. This does not establish complete animator playback or Android/client
equivalence. The catalog must be published before clients can download this approval.

## Catalog format

This is an example, not an approval:

```json
{
  "schemaVersion": 1,
  "revision": 2,
  "approved": [
    {
      "furyPlusPlus": "1.3.0",
      "vrcfury": ["1.1427.0", "1.1428.0"]
    }
  ]
}
```

Use exact three-part stable versions. Ranges, wildcards, prereleases, duplicate
rows and duplicate versions are rejected. Increase `revision` for every edit,
including formatting changes. A client with a cached revision rejects an older
revision or different content carrying the same revision. To withdraw an
approval, remove its pair and increase the revision; catalogs replace the old
list rather than merging with it.

## Review and publication

1. Compare the exact upstream release trees, including called helpers and build
   phase ordering. Matching method signatures alone do not prove compatibility.
2. Verify module installation, run EditMode tests, and compare representative
   avatar bakes with and without the optimizations. Check expected differences
   for enabled quality passes and bug-fix options. Record Unity and SDK versions.
3. Add only the tested pairs. If a patch needs adaptation, release a new
   FuryPlusPlus version and approve that version instead.
4. Publish the reviewed catalog change to `master` with explicit authorization.
   It becomes available through GitHub's raw-file endpoint without a package
   release or GitHub Pages deployment. Client/CDN caching can delay visibility.

Before publication, test the proposed catalog in the Development project by
seeding its local cache with the candidate JSON and the current UTC fetch time,
using the `CompatibilityCache` record format. Preserve the previous cache for
restoration and reload scripts before running installation and bake checks.
This enables the candidate only in that test project; it is not a public
approval. Do not publish the catalog merely to make the validation run possible.

## Client behavior

- The client requests one fixed public URL. It sends no avatar data or version
  query parameters and downloads no executable code. Normal HTTPS certificate
  validation remains enabled; redirects are disabled, the timeout is 15 seconds,
  and the response is capped at 256 KiB.
- The GitHub repository and HTTPS are the authority. The catalog is not
  independently signed. Access to publish this file must be restricted like
  access to publish the package itself.
- A project-local cache in `Library/FuryPlusPlus/compatibility-cache.json`
  survives Editor restarts. At initialization, a cache up to 30 days old can
  approve a pair. Missing, corrupt, future-dated or expired cache data cannot.
- Before any automatic request, a one-time prompt explains manual compatibility
  testing and asks permission to check GitHub. Allowing checks enables one
  request after Editor startup and each script reload. Declining is remembered
  without asking again. The choice is stored in EditorPrefs for this computer's
  user and can be changed in settings; restoring recommended module settings
  does not change it. Turning checks off cancels any pending request and keeps
  the cache. GitHub receives a normal web request, including the client's IP.
- **Check once on GitHub** in settings requests the list manually without
  enabling automatic checks. Batch mode never prompts or starts automatic
  requests. Failed checks leave the previous cache intact.
- A downloaded list is validated and saved atomically. It takes effect after
  the next script-domain reload or Editor restart. Master-switch toggles do not
  apply pending approvals; a network response never changes installed patches
  during a bake. Settings report whether a new decision is waiting to apply.
- Approval withdrawal is not instantaneous: offline clients can use a fresh
  cached approval, and running sessions keep their initial decision until a
  script reload. The 30-day age check applies at initialization.
- Unknown pairs run stock VRCFury with optimization modules disabled. Profiling
  and editor visuals remain eligible, subject to their existing member checks.

The VPM dependency range allows installation of newer VRCFury 1.x releases;
it does not approve them. Version labels identify official release pairs and
do not certify modified forks carrying the same package versions.
