# SupaClean — Windows storage and dependency inspector

**Current source: 0.3; every successful main-branch push automatically publishes a new numbered Windows installer.** C# / WinUI 3 / Windows 11-style Fluent interface.

SupaClean scans installed applications, explicit .NET runtime declarations, bounded native PE imports and managed package declarations, drive capacity, Downloads and the current user's temporary folder. It **does not infer that an unobserved dependency is absent**. Shared runtimes and drivers remain protected.

## SupaClean name migration

SupaClean is the new visible application name. The repository remains
[3AYZE/SmartClean](https://github.com/3AYZE/SmartClean), the internal WinUI
assembly remains SmartClean.WinUI, and the ONE downloadable installer remains
**SmartClean-Setup.exe** for compatibility with the 0.3.x updater.
The original installer AppId and installation directory are intentionally kept
so the upgrade replaces the existing installation in place. Start Menu and
desktop shortcuts are renamed; recovery and update history are preserved.

## Windows icon assets

SupaClean uses one simple Fluent-style identity: a blue/cyan rounded tile, a bold white **S**, and one small sparkle. The build generates a multi-resolution `SupaClean.ico` for the EXE, desktop shortcut and taskbar (16, 20, 24, 32, 40, 48, 64, 128 and 256 px). A separate `SupaClean-Tray.ico` keeps only the small Windows notification/background-task sizes (16–48 px), with the sparkle removed at the smallest sizes for legibility. The editable source is `src/SmartClean.WinUI/Assets/SupaClean.svg`; `tools/Generate-SupaCleanIcons.ps1` deterministically creates the Windows assets before every build.

The icon generator is part of the project build, so local builds and GitHub releases use the same artwork. Icon generation failure stops the build instead of silently shipping the generic executable icon.

## What works in current source

- **Overview:** app inventory, observed declarations, available space and a bounded folder scan. Folder errors identify affected folders.
- **Applications:** search registered software, review known dependencies and open Windows Settings > Installed apps for a user-approved uninstall. SupaClean never executes uninstall strings from the registry.
- **Cleanup:** inspect regular top-level .tmp, .temp, .log and .dmp files in your own LOCAL temporary folder. Eligible files must be at least 30 days old. The page shows eligible file names, locations, dates and sizes, plus read-only examples and counts for excluded files with specific reasons. Users select items, see the full review list, can uncheck individual entries and confirm before any move. No automatic removal.
- **Recovery:** move only reviewed and revalidated candidates into a per-user vault on the same volume, journaling the original path. Select which Recovery files to restore or delete. The final deletion dialog lists every chosen file, original path and size, permits individual deselection, and requires explicit confirmation. Existing files are never overwritten on restore.
- **Protection:** mandatory safeguards for shared runtimes, personal folders, linked paths and unknown dependencies. A second component inspector covers recognized registered Visual C++ (2013 and 2015–2022), WebView2, Windows App SDK, Java, Python, Vulkan and legacy DirectX components. It examines bounded, read-only native PE imports and managed dependency manifests for registered applications. Each relationship shows its supporting file and detection method. It cannot prove the exact installed redistributable was used, or that a runtime without evidence is unused.
- **Settings:** native light/dark theme, installed version and reviewed, hash-checked GitHub release updates.

**Moving a file to Recovery does not free disk space** while it remains on the same volume. Potential storage is reclaimed only after a separate confirmed permanent deletion. Locked, changed or ineligible files are skipped. There is no recursive cleanup, registry modification, background application usage tracking, full native DLL/dependency graph or automatic application uninstall.

## Build locally without publishing a release

On Windows, install the .NET 10 SDK and run Build-Windows.cmd in the repository root. It produces the unpackaged multi-file application under output/SmartClean-win-x64. Start SmartClean.WinUI.exe **with its dependencies beside it**. If no window appears, run Run-SmartClean-Diagnostics.cmd.

Run Test-Core-Windows.cmd to exercise runtime protection, bounded folder traversal, update integrity and the actual temporary-file move / restore / purge lifecycle.

## Dependency relationships

The new **Dependencies** page separates **Windows and background services**
(read-only service executable registration, not framework use), **shared
components** (.NET, Visual C++, WebView2, Java, Python, Windows App SDK and
graphics libraries), and **No detected dependents**. Python installer fragments
(core interpreter, development libraries and PATH option) are grouped by
version, architecture and install scope. The services section associates
registered executables with known application install folders and shows the
actual path. No-reference results explicitly remain protected: lack of matching
imports/declarations or unavailable installation evidence does NOT prove
anything is unused. Automatic uninstall decisions remain disabled.

## Drive scans and .NET runtime relationships

- In **Files & storage**, select C:, D:, other ready local fixed drives, or **All fixed drives**. This is an on-demand, bounded, read-only scan (45,000 entries per drive). Linked paths are not traversed. Incomplete drive totals are lower bounds, not estimates of reclaimable storage. Drive-wide scans never add files to the temporary-file deletion list.
- In **Protection**, each recognized installed modern .NET runtime shows registered applications whose runtimeconfig files explicitly declare a compatible major/minor framework family (for example Microsoft.NETCore.App 9.0.0 for an installed .NET 9.0.x runtime). Select a runtime to see app names, requested versions and evidence paths. This does not prove the exact installed patch was loaded, and zero observed declarations does not mean the runtime is unused or removable.
- Scan limitations list specific excluded paths and reasons where available. Linked paths are expected exclusions, whereas unreadable directories and entry limits produce partial-scan notices.

## What component evidence means

- **Declared .NET family:** application configuration requests a framework major/minor. This does not prove which installed patch was loaded.
- **Native PE import:** an app binary refers to a library such as vcruntime140.dll, WebView2Loader.dll, vulkan-1.dll, jvm.dll or python312.dll. The library could be bundled locally, or loaded only under certain conditions. Matching an import to a registered redistributable indicates possible usage of the component family, NOT proof that the separately installed package supplied it.
- **Managed package declaration:** an inspected dependency manifest lists WebView2 or Windows App SDK. An app may still bundle those dependencies.
- **Unverified:** an app's install path is missing/inaccessible, a binary loads DLLs dynamically, or only optional features need the runtime. Missing observed relationships never authorize a component uninstall.

Inspection is bounded to 12 binaries, 8 dependency manifests and 4 shallow directories per registered app, subject to file-size and app limits. Executables are never started and privileges are not elevated. Results are informational; dependency-based automatic uninstalls remain disabled.

## CI and release policy

- CI (.github/workflows/ci.yml) tests pull requests. The release workflow separately runs all safeguard tests and compiles every push to main.
- The release workflow (.github/workflows/release.yml) automatically builds and publishes every current main-branch update after passing tests (and also supports manual runs). Each successful latest-source build gets an increasing four-part version and **one** stable release asset, SmartClean-Setup.exe. Failed or superseded builds do not publish.
- The installed app checks the latest **public stable GitHub release** for a newer compiled installer, verifies its SHA-256 digest and byte count, and requires approval before launching it. GitHub Actions automatically builds source changes into a versioned installer; source alone cannot update an EXE. This repository is public, so no GitHub credentials are embedded.
- Builds are currently unsigned; install only files obtained from the official repository and check Windows warnings.

## Security scope

Cleanup candidates remain limited to the user's local temp root and an explicit allowlist; the excluded-file list is read-only and cannot bypass those rules. No subfolders are traversed or personal data automatically deleted. Metadata is rechecked before each move. Recovery is journaled and refuses overwrite. See docs/SECURITY.md for limitations and acceptance tests.

The project is in development. Passing CI establishes core behavior and Windows compilation, not interactive GUI, accessibility or long-duration desktop reliability.
