using System.Text.Json;

namespace SmartClean.Core.Cleanup;

// Deliberately narrow cleanup scope: ordinary files at the TOP LEVEL of the
// CURRENT USER's temporary directory, with selected temporary-file extensions,
// at least 30 days since last modification. No recursive deletion or app folders.
public sealed record TempCandidate(string FullPath, long Bytes, DateTime LastWriteUtc);
public sealed record TempExcludedFile(string FullPath, long Bytes, DateTime LastWriteUtc, string Reason);
public sealed record TempCandidateScan(
    IReadOnlyList<TempCandidate> Items, int Skipped, bool LimitReached, string? UnavailableReason)
{
    // File names and metadata only. Excluded files are never passed to removal methods.
    public IReadOnlyList<TempExcludedFile> ExcludedFiles { get; init; } = [];
    public IReadOnlyDictionary<string, int> ExclusionReasons { get; init; }
        = new Dictionary<string, int>();
    public int InspectedEntries { get; init; }
}
public sealed record RecoveryItem(
    string Id, string OriginalPath, long Bytes, DateTimeOffset MovedAtUtc, string State);

public sealed class TempRecoveryManager
{
    private const int MinAgeDays = 30;
    private const int MaxEntries = 15_000;
    private const int MaxCandidates = 300;
    private const int MaxExcludedPreview = 250;
    private const long MaxCandidateBytes = 1_073_741_824; // Limit a single move to 1 GiB.
    private const string JournalName = "journal.json";
    private const string PayloadName = "temporary-file.bin";
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tmp", ".temp", ".log", ".dmp" };

    private readonly string _tempRoot;
    private readonly string _vaultRoot;
    private readonly bool _testPaths;
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public TempRecoveryManager()
        : this(Path.GetTempPath(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClean", "Recovery"), testPaths: false) { }

    // Kept internal so release builds cannot be pointed at Downloads, projects
    // or arbitrary folders. The automated test assembly alone has access.
    internal TempRecoveryManager(string tempRoot, string vaultRoot, bool testPaths)
    {
        _tempRoot = Canonical(tempRoot);
        _vaultRoot = Canonical(vaultRoot);
        _testPaths = testPaths;
    }

    public TempCandidateScan FindCandidates(CancellationToken token = default)
    {
        var unsupported = RootProblem();
        if (unsupported is not null)
            return new TempCandidateScan([], 0, false, unsupported);

        var found = new List<TempCandidate>();
        var excluded = new List<TempExcludedFile>();
        var reasons = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var visited = 0;
        var limitReached = false;
        void RecordReason(string reason)
        {
            skipped++;
            reasons[reason] = reasons.TryGetValue(reason, out var n) ? n + 1 : 1;
        }

        try
        {
            // Only top-level regular files. Directories, including reparse-point
            // folders, are never traversed or presented as deletion candidates.
            foreach (var path in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (visited >= MaxEntries)
                {
                    limitReached = true;
                    break;
                }
                visited++;
                try
                {
                    var info = new FileInfo(path);
                    var reason = GetExclusionReason(path, info);
                    if (reason is null && found.Count >= MaxCandidates)
                        reason = "Candidate display limit reached";
                    if (reason is not null)
                    {
                        RecordReason(reason);
                        if (excluded.Count < MaxExcludedPreview)
                            excluded.Add(new TempExcludedFile(info.FullName, info.Length,
                                info.LastWriteTimeUtc, reason));
                        continue;
                    }
                    found.Add(new TempCandidate(info.FullName, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or PathTooLongException)
                {
                    RecordReason("Unreadable file metadata");
                    if (excluded.Count < MaxExcludedPreview)
                        excluded.Add(new TempExcludedFile(path, 0, DateTime.MinValue,
                            "Unreadable file metadata"));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or DirectoryNotFoundException)
        {
            return new TempCandidateScan(found, skipped + 1, limitReached,
                "The temporary folder could not be read completely: " + e.Message)
            {
                ExcludedFiles = excluded,
                ExclusionReasons = reasons,
                InspectedEntries = visited
            };
        }

        return new TempCandidateScan(
            found.OrderByDescending(x => x.Bytes).ToArray(), skipped, limitReached, null)
        {
            ExcludedFiles = excluded.OrderByDescending(x => x.Bytes).ToArray(),
            ExclusionReasons = reasons,
            InspectedEntries = visited
        };
    }

    public RecoveryItem MoveToRecovery(TempCandidate candidate)
    {
        ValidateVault();
        // An old scan result is never authority to move a changed file.
        var path = Canonical(candidate.FullPath);
        if (!IsCandidatePath(path)) throw new InvalidOperationException(
            "This file is no longer an eligible temporary-file candidate. Scan again.");
        var info = new FileInfo(path);
        if (info.Length != candidate.Bytes || info.LastWriteTimeUtc != candidate.LastWriteUtc)
            throw new InvalidOperationException("The file changed since the scan. Scan again.");

        var id = Guid.NewGuid().ToString("N");
        var bucket = Path.Combine(_vaultRoot, id);
        var payload = Path.Combine(bucket, PayloadName);
        Directory.CreateDirectory(bucket);
        var item = new RecoveryItem(id, path, info.Length, DateTimeOffset.UtcNow, "pending");
        SaveJournal(bucket, item); // Write intent BEFORE the move for crash recovery.
        try
        {
            // If an application holds this file, Windows usually rejects the
            // move. Do not terminate applications, elevate or force ownership.
            File.Move(path, payload);
            item = item with { State = "stored" };
            SaveJournal(bucket, item);
            return item;
        }
        catch
        {
            // A pending journal with a payload is recoverable on next launch.
            // Do not silently delete either copy.
            throw;
        }
    }

    public IReadOnlyList<RecoveryItem> ListRecovery()
    {
        if (!Directory.Exists(_vaultRoot) || IsLinked(_vaultRoot)) return [];
        var found = new List<RecoveryItem>();
        foreach (var bucket in Directory.EnumerateDirectories(_vaultRoot))
        {
            if (IsLinked(bucket)) continue;
            if (!Guid.TryParseExact(Path.GetFileName(bucket), "N", out _)) continue;
            try
            {
                var item = ReadJournal(bucket);
                if (item is null || !item.Id.Equals(Path.GetFileName(bucket),
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (item.State is not ("pending" or "stored")) continue;
                var payload = Path.Combine(bucket, PayloadName);
                if (File.Exists(payload) && !IsLinked(payload) && IsUnderTempRoot(item.OriginalPath))
                    found.Add(item);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or JsonException or System.Security.SecurityException)
            { /* Damaged journal is never a reason to delete a payload. */ }
        }
        return found.OrderByDescending(x => x.MovedAtUtc).ToArray();
    }

    public void Restore(string id)
    {
        var (bucket, item, payload) = GetStoredItem(id);
        var original = Canonical(item.OriginalPath);
        if (!IsUnderTempRoot(original) || IsLinked(_tempRoot))
            throw new InvalidOperationException("The original folder is no longer trusted.");
        if (File.Exists(original) || Directory.Exists(original))
            throw new IOException("A file already exists at the original location. No overwrite was attempted.");
        // Restoring may recreate the user's temporary folder, never a project
        // directory or arbitrary journal-supplied location.
        Directory.CreateDirectory(_tempRoot);
        File.Move(payload, original);
        SaveJournal(bucket, item with { State = "restored" });
    }

    // Only a *separate*, explicit confirmation in the UI may call this.
    // Never deletes by original path, never recursively deletes directories.
    public long PurgeFromRecovery(string id)
    {
        var (bucket, item, payload) = GetStoredItem(id);
        var size = new FileInfo(payload).Length;
        File.Delete(payload);
        SaveJournal(bucket, item with { State = "purged" });
        return size;
    }

    private (string Bucket, RecoveryItem Item, string Payload) GetStoredItem(string id)
    {
        ValidateVault();
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid recovery identifier.", nameof(id));
        var bucket = Path.Combine(_vaultRoot, id);
        var payload = Path.Combine(bucket, PayloadName);
        if (IsLinked(bucket) || IsLinked(payload))
            throw new InvalidOperationException("Linked recovery paths are blocked.");
        var item = ReadJournal(bucket);
        if (item is null || !id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
            || item.State is not ("pending" or "stored") || !File.Exists(payload)
            || !IsUnderTempRoot(item.OriginalPath))
            throw new InvalidOperationException("Recovery item is missing or its journal is invalid.");
        return (bucket, item, payload);
    }

    private void ValidateVault()
    {
        var problem = RootProblem();
        if (problem is not null) throw new InvalidOperationException(problem);
        Directory.CreateDirectory(_vaultRoot);
        if (IsLinked(_vaultRoot))
            throw new InvalidOperationException("Linked recovery folders are not supported.");
    }

    private string? RootProblem()
    {
        if (!OperatingSystem.IsWindows() && !_testPaths)
            return "Temporary-file cleanup is available only on Windows.";
        if (string.Equals(_tempRoot, _vaultRoot, StringComparison.OrdinalIgnoreCase)
            || _vaultRoot.StartsWith(_tempRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return "The recovery location must be outside temporary storage.";
        if (!string.Equals(Path.GetPathRoot(_tempRoot), Path.GetPathRoot(_vaultRoot),
                StringComparison.OrdinalIgnoreCase))
            return "Temp and recovery must be on the same volume to avoid copying files.";
        if (!_testPaths)
        {
            var local = Canonical(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData));
            if (!_tempRoot.StartsWith(local + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return "Only the current user's local temporary folder can be cleaned.";
        }
        if (!Directory.Exists(_tempRoot) || IsLinked(_tempRoot))
            return "Temporary folder is missing or is a linked directory.";
        return null;
    }

    private bool IsUnderTempRoot(string path)
    {
        try
        {
            var full = Canonical(path);
            return string.Equals(Path.GetDirectoryName(full), _tempRoot,
                StringComparison.OrdinalIgnoreCase) && Extensions.Contains(Path.GetExtension(full));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
            or PathTooLongException) { return false; }
    }

    private bool IsCandidatePath(string path)
    {
        if (!IsUnderTempRoot(path) || IsLinked(path)) return false;
        return GetExclusionReason(path, new FileInfo(path)) is null;
    }

    private string? GetExclusionReason(string path, FileInfo info)
    {
        if (!IsUnderTempRoot(path)) return "Not a supported temporary-file type (.tmp, .temp, .log, .dmp)";
        if (!info.Exists) return "File no longer exists";
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return "Linked file — protected";
        if ((info.Attributes & (FileAttributes.System | FileAttributes.Hidden |
                                 FileAttributes.ReadOnly | FileAttributes.Directory)) != 0)
            return "Protected file attributes";
        if (info.Length <= 0) return "Empty file — no storage to recover";
        if (info.Length > MaxCandidateBytes) return "Larger than the 1 GiB per-file limit";
        if (info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-MinAgeDays))
            return "Modified within the last 30 days";
        return null;
    }

    private static string Canonical(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsLinked(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static RecoveryItem? ReadJournal(string bucket)
    {
        var file = Path.Combine(bucket, JournalName);
        return File.Exists(file)
            ? JsonSerializer.Deserialize<RecoveryItem>(File.ReadAllText(file), JournalOptions)
            : null;
    }

    private static void SaveJournal(string bucket, RecoveryItem item)
    {
        var journal = Path.Combine(bucket, JournalName);
        var pending = Path.Combine(bucket, "journal-writing.tmp");
        File.WriteAllText(pending, JsonSerializer.Serialize(item, JournalOptions));
        File.Move(pending, journal, overwrite: true);
    }
}
