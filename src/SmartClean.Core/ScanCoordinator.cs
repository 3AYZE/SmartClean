namespace SmartClean.Core;

public sealed class ScanCoordinator
{
    public async Task<ScanSnapshot> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? selectedFixedDriveRoots = null)
    {
        // All expensive IO stays outside the UI thread.
        return await Task.Run(() => Scan(progress, cancellationToken,
            selectedFixedDriveRoots ?? []), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ScanSnapshot Scan(IProgress<string>? progress, CancellationToken ct,
        IReadOnlyList<string> selectedFixedDriveRoots)
    {
        var warnings = new List<string>();
        progress?.Report("Reading installed applications...");
        var apps = new InstalledAppScanner().Scan(ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Inspecting explicit .NET dependency declarations...");
        var dependencies = new DependencyInspector().Inspect(apps, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Inspecting native DLL imports and package references...");
        var componentScan = new ComponentInspector().Inspect(apps, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Measuring Downloads and temporary folders...");
        var scanner = new FolderScanner();
        var folders = scanner.Scan(FolderScanner.DefaultTargets(), ct).ToList();
        ct.ThrowIfCancellationRequested();
        if (selectedFixedDriveRoots.Count > 0)
        {
            var drives = DriveScanCatalog.SelectFixedRoots(
                DriveScanCatalog.GetFixedReadyRoots(), selectedFixedDriveRoots);
            foreach (var drive in drives)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Measuring " + drive.Label + " (read-only, bounded)...");
                folders.AddRange(scanner.Scan([drive], ct,
                    maxEntriesPerRoot: DriveScanCatalog.DriveEntryLimit));
            }
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report("Reading drive capacity...");
        var disks = new List<DiskFinding>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    disks.Add(new DiskFinding(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { warnings.Add("Drive capacity could not be read for one drive."); }
        }
        // Zero observed declarations is expected for many installations;
        // it is a scope limitation, not a failed scan. Protection explains it.
        // Linked paths intentionally skipped do not trigger an alarming warning.
        // Each path and its reason remains visible on Protection.
        foreach (var folder in folders)
        {
            var unexpected = folder.Issues.Count(i => !i.IsExpected);
            if (unexpected > 0 || folder.Truncated)
                warnings.Add($"{folder.Label}: {unexpected:N0} unreadable/truncated issue(s)"
                    + (folder.Truncated ? "; entry limit reached; measured bytes are a lower bound" : ""));
        }
        progress?.Report("Scan complete. No files or applications were modified.");
        return new ScanSnapshot(DateTimeOffset.Now, apps, dependencies, folders, disks, warnings)
        {
            OtherComponentScan = componentScan
        };
    }
}
