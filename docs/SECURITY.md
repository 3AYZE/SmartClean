# SupaClean 0.4 — safety model and Windows QA

## Mandatory constraints

1. Candidates are top-level regular .tmp/.temp/.log/.dmp files under the current user's local temp folder, last modified at least 30 days ago. No recursive or generalized old-file deletion. Reparse roots and file reparse points are blocked; enumeration is bounded.
2. File age is not proof of non-use. The user selects proposed items and sees a complete second-review list with individual checkboxes before any move to Recovery. Excluded files appear in a read-only list with reason counts and cannot be selected. Changed, ineligible or in-use files are rechecked and skipped.
3. The recovery vault is under LOCALAPPDATA/SmartClean/Recovery, on the same volume and outside the user's temp folder. A per-file JSON journal records the original path before moving. An interrupted move can leave a pending journal; items with a stored payload remain recoverable.
4. Restoration refuses to overwrite any existing original path. Permanent deletion is restricted to the selected vault payload identified by an internal GUID and requires a second explicit file-by-file review with individual deselection. Moving to Recovery alone frees no disk space.
5. Scanning never requires elevation or changes the registry, drivers, Windows services or application installations. Unverified dependencies stay unverified. SmartClean only offers to open Windows Settings for user-managed application uninstallation.
6. No untrusted package source, telemetry, silent cleanup, embedded GitHub token or silent EXE replacement. Main-branch source pushes trigger test/build/release, but the installed application requires the user's approval before running a verified update.
7. These protections reduce accidental data loss. They are not a sandbox against software with full access to the same user's account or against malicious edits to the recovery journal.

## Read-only drive scan and dependency evidence

- Only selected ready fixed local drive roots are accepted; selection is validated again before scanning. Every drive-wide scan is bounded to 45,000 entries, cancelable, read-only and never expands file deletion beyond the approved temp-folder allowlist. Network/removable drives and arbitrary path inputs are excluded.
- A .NET runtime usage relationship means an inspected registered application explicitly requests the matching framework major/minor via its runtimeconfig file. Do not describe the relationship as proof a specific installed patch was loaded, and never remove an unobserved runtime automatically.
- Native-component inspection reads bounded PE import descriptors and selected dependency manifests under registered install locations. It never executes binaries, follows linked roots, elevates privileges, or claims that a particular installed package supplied an imported DLL.
- Visual C++ architectures match only compatible x86/x64 import evidence. Java, Python, WebView2, Windows App SDK and graphics components may be bundled or dynamically loaded; unmatched or unknown components remain protected.
- Capture a bounded list of excluded filesystem paths and reasons. Linked paths are intentionally skipped without an alarming warning; unreadable paths and truncated totals indicate partial coverage and must be labeled accordingly.

## Branding and relationship safety

- The installer must preserve its existing AppId, binary name and install path
  while adopting the SupaClean display name, so installed 0.3.x users receive
  this version using the existing one-EXE release filename and update checker.
- Windows services are read as registration metadata only. Matching an
  executable to a registered install location demonstrates a folder association,
  not proof that the service loads any particular dependency.
- Python installer fragments are grouped conservatively by version, processor
  architecture, and per-machine/per-user installation scope. An empty inbound
  reference list is never a deletion recommendation.

## Automated core checks

- [x] Shared runtimes remain protected even without observed dependent apps.
- [x] Explicit .NET frameworks are parsed correctly and self-contained frameworks are not counted as external dependencies.
- [x] Folder scans are bounded, cancelable and skip linked directories.
- [x] Files outside the .tmp/.temp/.log/.dmp allowlist, nested files and recently modified files cannot become cleanup candidates.
- [x] Excluded top-level files have categorized reasons but cannot be moved through the cleanup engine.
- [x] Old, regular top-level logs/dumps can be reviewed and restored, while unknown file types remain excluded.
- [x] A file changed since scanning is rejected at move time.
- [x] Moved files have a journal and recoverable vault payload.
- [x] Restoration refuses overwrite of existing original paths.
- [x] Permanent deletion is limited to explicitly selected vault payloads; invalid IDs are rejected.

## Manual Windows checks still needed

- [ ] Launch as a standard user from a clean profile with no Visual Studio present.
- [ ] Test keyboard navigation, high contrast, reduced motion, scaling, light/dark theme, small windows and sidebar collapse.
- [ ] Test locked temporary files, full disks, standard permissions and inaccessible roots; errors must be clear and UI responsive.
- [ ] Simulate interruption immediately before and after a move and journal update; inspect pending journals.
- [ ] Test an existing file at restoration path and a removed or redirected original temp root.
- [ ] Test junctions, reparse points, redirected app data and external drives; recovery must stay out of the candidate root on the same volume.
- [ ] Check protected components cannot be routed through the app's uninstall button and registry uninstall commands are never run.
- [ ] Confirm each main-branch push creates at most one installer only after passing tests and the installed app updates only following approval of a verified compiled stable release.
