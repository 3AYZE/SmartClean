# SmartClean — Windows storage and dependency inspector

**Current source: 0.3 (unreleased)**. C# / WinUI 3 / Windows 11-style Fluent interface.

SmartClean scans installed applications, explicit .NET runtime declarations, drive capacity, Downloads and the current user's temporary folder. It **does not infer that an unobserved dependency is absent**. Shared runtimes and drivers remain protected.

## What works in current source

- **Overview:** app inventory, observed declarations, available space and a bounded folder scan. Folder errors identify affected folders.
- **Applications:** search registered software, review known dependencies and open Windows Settings > Installed apps for a user-approved uninstall. SmartClean never executes uninstall strings from the registry.
- **Cleanup:** only top-level .tmp and .temp files directly inside the current user's LOCAL temporary folder, last modified at least 30 days ago. Age does not prove that a file is unused: every move requires selection and confirmation.
- **Recovery:** move each eligible, revalidated selected candidate into a dedicated per-user vault on the same volume, journaling the original path before the move. Restore without overwriting existing files. Permanently delete only a specifically selected stored payload after another confirmation.
- **Protection:** mandatory safeguards for shared runtimes, personal folders, linked paths and unknown dependencies. Ordinary scans do not require elevation.
- **Settings:** native light/dark theme, installed version and reviewed, hash-checked GitHub release updates.

**Moving a file to Recovery does not free disk space** while it remains on the same volume. Potential storage is reclaimed only after a separate confirmed permanent deletion. Locked, changed or ineligible files are skipped. There is no recursive cleanup, registry modification, background application usage tracking, native DLL dependency graph or automatic application uninstall.

## Build locally without publishing a release

On Windows, install the .NET 10 SDK and run Build-Windows.cmd in the repository root. It produces the unpackaged multi-file application under output/SmartClean-win-x64. Start SmartClean.WinUI.exe **with its dependencies beside it**. If no window appears, run Run-SmartClean-Diagnostics.cmd.

Run Test-Core-Windows.cmd to exercise runtime protection, bounded folder traversal, update integrity and the actual temporary-file move / restore / purge lifecycle.

## CI and release policy

- The CI workflow (.github/workflows/ci.yml) tests and compiles Windows x64 on source pushes and pull requests. **It does not publish or update the installed application.**
- The release workflow (.github/workflows/release.yml) runs **only when manually dispatched**. When manually started and successful, it produces a single installer asset: SmartClean-Setup.exe. Source commits do not create releases.
- The installed app checks the latest **public stable GitHub release** for a newer compiled installer, verifies its SHA-256 digest and byte count, and requires approval before launching it. **A GitHub source commit cannot update an installed EXE.** This repository is private: token-free release checks require public release distribution. No GitHub credentials are embedded.
- Builds are currently unsigned; install only files obtained from the official repository and check Windows warnings.

## Security scope

File cleanup is limited to the user's local temp root, without descending into subfolders or automatically deleting personal data. Metadata is rechecked before each move. Recovery is journaled and refuses overwrite. See docs/SECURITY.md for limitations and acceptance tests.

The project is in development. Passing CI establishes core behavior and Windows compilation, not interactive GUI, accessibility or long-duration desktop reliability.
