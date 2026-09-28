# SmartClean — Windows storage and dependency inspector

**Current source: 0.3; every successful main-branch push automatically publishes a new numbered Windows installer.** C# / WinUI 3 / Windows 11-style Fluent interface.

SmartClean scans installed applications, explicit .NET runtime declarations, drive capacity, Downloads and the current user's temporary folder. It **does not infer that an unobserved dependency is absent**. Shared runtimes and drivers remain protected.

## What works in current source

- **Overview:** app inventory, observed declarations, available space and a bounded folder scan. Folder errors identify affected folders.
- **Applications:** search registered software, review known dependencies and open Windows Settings > Installed apps for a user-approved uninstall. SmartClean never executes uninstall strings from the registry.
- **Cleanup:** inspect regular top-level .tmp, .temp, .log and .dmp files in your own LOCAL temporary folder. Eligible files must be at least 30 days old. The page shows eligible file names, locations, dates and sizes, plus read-only examples and counts for excluded files with specific reasons. Users select items, see the full review list, can uncheck individual entries and confirm before any move. No automatic removal.
- **Recovery:** move only reviewed and revalidated candidates into a per-user vault on the same volume, journaling the original path. Select which Recovery files to restore or delete. The final deletion dialog lists every chosen file, original path and size, permits individual deselection, and requires explicit confirmation. Existing files are never overwritten on restore.
- **Protection:** mandatory safeguards for shared runtimes, personal folders, linked paths and unknown dependencies. Ordinary scans do not require elevation.
- **Settings:** native light/dark theme, installed version and reviewed, hash-checked GitHub release updates.

**Moving a file to Recovery does not free disk space** while it remains on the same volume. Potential storage is reclaimed only after a separate confirmed permanent deletion. Locked, changed or ineligible files are skipped. There is no recursive cleanup, registry modification, background application usage tracking, native DLL dependency graph or automatic application uninstall.

## Build locally without publishing a release

On Windows, install the .NET 10 SDK and run Build-Windows.cmd in the repository root. It produces the unpackaged multi-file application under output/SmartClean-win-x64. Start SmartClean.WinUI.exe **with its dependencies beside it**. If no window appears, run Run-SmartClean-Diagnostics.cmd.

Run Test-Core-Windows.cmd to exercise runtime protection, bounded folder traversal, update integrity and the actual temporary-file move / restore / purge lifecycle.

## Drive scans and .NET runtime relationships

- In **Files & storage**, select C:, D:, other ready local fixed drives, or **All fixed drives**. This is an on-demand, bounded, read-only scan (45,000 entries per drive). Linked paths are not traversed. Incomplete drive totals are lower bounds, not estimates of reclaimable storage. Drive-wide scans never add files to the temporary-file deletion list.
- In **Protection**, each recognized installed modern .NET runtime shows registered applications whose runtimeconfig files explicitly declare a compatible major/minor framework family (for example Microsoft.NETCore.App 9.0.0 for an installed .NET 9.0.x runtime). Select a runtime to see app names, requested versions and evidence paths. This does not prove the exact installed patch was loaded, and zero observed declarations does not mean the runtime is unused or removable.
- Scan limitations list specific excluded paths and reasons where available. Linked paths are expected exclusions, whereas unreadable directories and entry limits produce partial-scan notices.

## CI and release policy

- CI (.github/workflows/ci.yml) tests pull requests. The release workflow separately runs all safeguard tests and compiles every push to main.
- The release workflow (.github/workflows/release.yml) automatically builds and publishes every current main-branch update after passing tests (and also supports manual runs). Each successful latest-source build gets an increasing four-part version and **one** stable release asset, SmartClean-Setup.exe. Failed or superseded builds do not publish.
- The installed app checks the latest **public stable GitHub release** for a newer compiled installer, verifies its SHA-256 digest and byte count, and requires approval before launching it. GitHub Actions automatically builds source changes into a versioned installer; source alone cannot update an EXE. This repository is public, so no GitHub credentials are embedded.
- Builds are currently unsigned; install only files obtained from the official repository and check Windows warnings.

## Security scope

Cleanup candidates remain limited to the user's local temp root and an explicit allowlist; the excluded-file list is read-only and cannot bypass those rules. No subfolders are traversed or personal data automatically deleted. Metadata is rechecked before each move. Recovery is journaled and refuses overwrite. See docs/SECURITY.md for limitations and acceptance tests.

The project is in development. Passing CI establishes core behavior and Windows compilation, not interactive GUI, accessibility or long-duration desktop reliability.
