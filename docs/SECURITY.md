# Safety model and manual Windows test checklist

## Mandatory guarantees for v0.1

1. No destructive APIs in the app, no calls to an uninstaller, no reg writes and no privileged services.
2. Unreadable registry entries, runtime configs or folders cannot cause an app to be marked dependency-free.
3. Scans do not follow directory reparse points or intentionally open arbitrary personal file contents. Bounded `.runtimeconfig.json` parsing is an exception for explicit dependency metadata in registered install locations.
4. File age is based on last modification, **not usage**. No usage claims or automatic removal recommendations.
5. Scan cancellation and failure preserve the previous completed snapshot; incomplete measurements are labeled.
6. Do not elevate privileges for a normal scan or write any cloud/telemetry records.

## Manual QA on an actual Windows 11 machine

- [ ] Build and launch x64 release; no Visual Studio needed on target machine after self-contained publish.
- [ ] Test light/dark/system switching, resized window, collapsed sidebar, keyboard navigation and reduced-motion Windows setting.
- [ ] Scan standard account and admin account: no UAC prompts for ordinary scan; inaccessible paths show warning rather than crash.
- [ ] Verify installed app list against Apps & Features, including 32/64-bit and per-user installs. Note portable applications are *not* in inventory.
- [ ] Install a test framework-dependent .NET app with runtimeconfig and verify the version/evidence appears in the details panel.
- [ ] Check known Microsoft .NET, VC++ and WebView2 runtime entries are protected regardless of observed dependent app count.
- [ ] Create Downloads nested folders, symlink/junction to an external path and an inaccessible folder; ensure no traversal across links and partial results are flagged.
- [ ] Cancel halfway through a scan and confirm the previous snapshot remains visible.
- [ ] Scan with nearly full disk, removable drives and redirected Downloads; the latter is a documented unsupported case in this release.
- [ ] Confirm Task Manager CPU/disk usage during normal scan; UI remains responsive and no scanner remains after app exits.
- [ ] Run core test harness. Build must not ship until all automated tests and relevant manual Windows tests pass.

## Future destructive operations: release blockers

- Structured allowlist for OS-supported disposable cache types and user-specific eligibility.
- Per-item preview, in-use check, file identity verification immediately before deletion, symlink protections and TOCTOU defenses.
- Recycle Bin or dedicated recovery area where actually supported, with audit journal and integrity tests. Do not promise application uninstall rollback.
- Official registered uninstaller use only. No direct removal of Windows servicing components, drivers, shared runtimes, WinSxS or MSI internal caches.
- User-initiated approval for application uninstall, downloads, projects and any file with unknown ownership.
