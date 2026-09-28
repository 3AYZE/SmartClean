namespace SmartClean.Core;

// Whitelists only currently ready, fixed local volumes. The drive-wide scan
// is inventory-only; it never expands TempRecoveryManager's deletion scope.
public static class DriveScanCatalog
{
    public const int DriveEntryLimit = 45_000;

    public static IReadOnlyList<string> GetFixedReadyRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    roots.Add(Path.GetPathRoot(Path.GetFullPath(drive.RootDirectory.FullName))!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or ArgumentException or System.Security.SecurityException)
            {
                // An unavailable volume must not crash the chooser.
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<FolderTarget> SelectFixedRoots(
        IReadOnlyList<string> availableFixedRoots, IEnumerable<string> requestedRoots)
    {
        var allowed = availableFixedRoots
            .Select(x => Path.GetPathRoot(Path.GetFullPath(x))!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new List<FolderTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in requestedRoots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                throw new ArgumentException("An empty drive cannot be selected.");
            var full = Path.GetFullPath(candidate);
            var root = Path.GetPathRoot(full)!;
            if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !allowed.Contains(root))
                throw new ArgumentException("Only currently available fixed-drive roots can be selected.");
            if (seen.Add(root))
                selected.Add(new FolderTarget("Drive " + root, root, IsPersonalData: true));
        }
        return selected;
    }
}
