namespace SmartClean.Core;

public sealed class FolderScanner
{
    public const int DefaultMaximumEntriesPerRoot = 150_000;

    public static IReadOnlyList<FolderTarget> DefaultTargets()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var temp = Path.GetTempPath();
        return new[]
        {
            new FolderTarget("Downloads", downloads, IsPersonalData: true),
            new FolderTarget("Temporary files", temp, IsPersonalData: false)
        }.GroupBy(t => Normalize(t.Path), StringComparer.OrdinalIgnoreCase)
         .Select(g => g.First()).ToArray();
    }

    // Only supplied roots are scanned. Junctions/symlinks are NOT followed.
    public IReadOnlyList<FolderFinding> Scan(
        IEnumerable<FolderTarget> roots,
        CancellationToken cancellationToken = default,
        int maxEntriesPerRoot = DefaultMaximumEntriesPerRoot,
        DateTime? nowUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntriesPerRoot);
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-180);
        var results = new List<FolderFinding>();
        foreach (var target in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ScanOne(target, cutoff, maxEntriesPerRoot, cancellationToken));
        }
        return results;
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static FolderFinding ScanOne(
        FolderTarget target, DateTime cutoffUtc, int maxEntries, CancellationToken token)
    {
        long bytes = 0;
        var files = 0;
        var old = 0;
        var skipped = 0;
        var entries = 0;
        var truncated = false;
        var issues = new List<ScanIssue>();
        void RecordIssue(string path, string reason, bool expected)
        {
            if (issues.Count < 24) issues.Add(new ScanIssue(path, reason, expected));
        }
        var pending = new Stack<string>();
        try
        {
            var root = new DirectoryInfo(target.Path);
            if (!root.Exists)
                return new FolderFinding(target.Label, target.Path, 0, 0, 0, 0, false,
                    target.IsPersonalData, "Folder not found. No action available.");
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                return new FolderFinding(target.Label, target.Path, 0, 0, 0, 1, false,
                    target.IsPersonalData, "Linked root not scanned. Choose a real folder instead.")
                { Issues = [new ScanIssue(target.Path, "Linked root intentionally not followed", true)] };
            pending.Push(root.FullName);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                IEnumerator<FileSystemInfo>? items = null;
                try { items = new DirectoryInfo(current).EnumerateFileSystemInfos().GetEnumerator(); }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException
                    or System.Security.SecurityException or PathTooLongException)
                {
                    skipped++;
                    RecordIssue(current, "Directory unreadable: " + e.GetType().Name, false);
                    continue;
                }
                using (items)
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        FileSystemInfo entry;
                        try
                        {
                            if (!items.MoveNext()) break;
                            entry = items.Current;
                        }
                        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                            or System.Security.SecurityException or PathTooLongException)
                        {
                            skipped++;
                            RecordIssue(current, "Directory enumeration failed: " + e.GetType().Name, false);
                            break;
                        }
                        if (++entries > maxEntries)
                        {
                            truncated = true;
                            RecordIssue(target.Path, "Scan entry limit reached; measured size is partial", false);
                            break;
                        }
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                skipped++;
                                RecordIssue(entry.FullName, "Linked file or folder intentionally not followed", true);
                                continue;
                            }
                            if (entry is DirectoryInfo directory)
                            {
                                pending.Push(directory.FullName);
                            }
                            else if (entry is FileInfo file)
                            {
                                files++;
                                // Saturate if a damaged filesystem reports implausible lengths.
                                bytes = file.Length > long.MaxValue - bytes ? long.MaxValue : bytes + file.Length;
                                if (file.LastWriteTimeUtc < cutoffUtc) old++;
                            }
                        }
                        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                            or System.Security.SecurityException or PathTooLongException)
                        {
                            skipped++;
                            RecordIssue(entry.FullName, "Entry unreadable: " + e.GetType().Name, false);
                        }
                    }
                }
                if (truncated) break;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or System.Security.SecurityException or ArgumentException or PathTooLongException)
        {
            skipped++;
            RecordIssue(target.Path, "Root scan interrupted: " + e.GetType().Name, false);
        }

        var recommendation = target.IsPersonalData
            ? "Personal files. Inspect individually; file age alone is not evidence of disuse."
            : "Temporary storage estimate only. Individual files need a separate safety check before cleanup.";
        if (truncated) recommendation += " Entry limit reached; size is incomplete.";
        return new FolderFinding(target.Label, target.Path, bytes, files, old, skipped,
            truncated, target.IsPersonalData, recommendation)
        { Issues = issues };
    }
}
