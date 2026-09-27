# SmartClean 0.2 — Windows storage and dependency inspector

Local, read-only WinUI 3 Windows desktop app. **It cannot delete, uninstall, quarantine, or modify scanned files or applications.** Dependency detection is partial: missing .NET runtime declarations do not prove a component is unused.

## Distribution and updates

- GitHub Actions builds the source on Windows on every push to `main`, runs the core safeguard tests, and if successful publishes **one** release asset: `SmartClean-Setup.exe`.
- The release EXE is a **per-user installer**, bundling the entire unpackaged, self-contained WinUI publish output. Install to `%LOCALAPPDATA%\Programs\SmartClean` with Start-menu shortcut, no administrator access required.
- SmartClean checks `3AYZE/SmartClean`'s latest **stable public** GitHub release when launched, and offers manual Check for updates in Settings. When a newer version is found, **Download and install** downloads the setup EXE, verifies GitHub's SHA-256 asset digest and exact file size, then opens the installer for your approval. It never silently runs an installer, does not hold a GitHub token, and does not modify user content.
- **IMPORTANT:** This repository is currently private. GitHub's public releases API cannot provide updates from a private repository without credentials. To enable token-free updates for installed users, make the repository public in GitHub Settings; never embed a private access token in a distributed executable.
- GitHub Actions must be enabled, and the workflow must have `contents:write` permission for `gh release create`. If the workflow or its tests fail, **no update is released**.
- This is an unsigned installer until code signing is configured. Windows SmartScreen may warn about an unsigned/new application. Download only from this repository's Releases page.
- App update checks operate only while SmartClean is open; the application adds no startup background service. No downloaded update is installed without user approval.

## Build on Windows

Prerequisites: Windows 10 19041+ or Windows 11; .NET 10 SDK is recommended (local build supports .NET 9 as a temporary fallback); internet for first NuGet restore. Run `Build-Windows.cmd`, which produces a **multi-file unpackaged publish folder** at `output\SmartClean-win-x64`. For an installable single EXE, install Inno Setup 6, then run:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "installer\SmartClean.iss"
```

The installer is created at `output\releases\SmartClean-Setup.exe`. GitHub Actions performs both operations automatically and assigns a distinct assembly/release version for every successful main-branch run.

Run `Test-Core-Windows.cmd` for core policy tests. If no window appears after launch, run `Run-SmartClean-Diagnostics.cmd` and inspect the local logs. Never copy only `SmartClean.WinUI.exe` from the publish folder; its native runtime dependencies must remain alongside it. The **installer** is the only single file you need to download.

## What is currently implemented

- Five-page native Windows 11 Fluent/Mica UI: Overview, Applications, Files & storage, Protection and Settings.
- Cancelable on-demand scans of Windows uninstall records, bounded explicit `.runtimeconfig.json` dependency declarations, fixed-drive capacity, Downloads and temporary folders.
- Conservative protection labels for known shared runtimes. Partial scans are disclosed; none of the UI's size and age evidence is approval to delete.
- New GitHub release check, exact-origin installer URL allowlist, digest and size verification, visible user-approved update flow.

## Scope and limitations

- No cleanup/removal operations, background usage tracking, real deep dependency analysis, or elevation mode in this release. Protection rules cannot be disabled. The Protection page reports incomplete evidence rather than offering a misleading enable toggle.
- GitHub Actions publishes only after a successful Windows build and core safeguards check. WinUI GUI launch verification on an interactive desktop should be performed before recommending a public release.
- Code signing and rollback of previous installers are not yet implemented. Inno Setup creates the standard per-user uninstaller but application data stays in `%LOCALAPPDATA%\SmartClean`.
- Update integrity uses the SHA-256 digest reported by GitHub; it does not substitute for publisher code signing.

## Layout

`src/SmartClean.Core`: read-only scanning, dependency detection, update metadata verification and installer downloading. `src/SmartClean.WinUI`: Windows UI and update review. `installer/SmartClean.iss`: single-EXE per-user installer. `.github/workflows/release.yml`: CI test/build/release. `tests/SmartClean.Core.Tests`: safety and updater regression tests. `docs/`: diagnostic and security notes.
