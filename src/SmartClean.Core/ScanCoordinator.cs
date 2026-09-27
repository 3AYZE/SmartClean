namespace SmartClean.Core;

public sealed class ScanCoordinator
{
    public async Task<ScanSnapshot> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // All expensive IO stays outside the UI thread.
        return await Task.Run(() => Scan(progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ScanSnapshot Scan(IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        progress?.Report("Reading installed applications...");
        var apps = new InstalledAppScanner().Scan(ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Inspecting explicit .NET dependency declarations...");
        var dependencies = new DependencyInspector().Inspect(apps, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Measuring Downloads and temporary folders...");
        var folders = new FolderScanner().Scan(FolderScanner.DefaultTargets(), ct);
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
        if (dependencies.Count == 0)
            warnings.Add("No explicit .NET runtimeconfig dependencies were observed. This does not mean applications have no dependencies.");
        if (folders.Any(f => f.Truncated || f.SkippedEntries > 0))
            warnings.Add("One or more folders could not be measured completely; some sizes are lower bounds.");
        progress?.Report("Scan complete. No files or applications were modified.");
        return new ScanSnapshot(DateTimeOffset.Now, apps, dependencies, folders, disks, warnings);
    }
}
