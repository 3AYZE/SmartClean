namespace SmartClean.Core;

// Inventory is evidence, not permission to uninstall software.
public sealed record InstalledApp(
    string Id,
    string Name,
    string Publisher,
    string Version,
    string? InstallLocation,
    long? EstimatedSizeBytes,
    DateTime? InstalledOn,
    string RegistryLocation,
    bool IsProtected,
    string ProtectionReason,
    string? UninstallCommand);

public sealed record DependencyEvidence(
    string AppId,
    string AppName,
    string FrameworkName,
    string RequestedVersion,
    string EvidencePath);

public sealed record FolderTarget(string Label, string Path, bool IsPersonalData);

public sealed record FolderFinding(
    string Label,
    string Path,
    long Bytes,
    int Files,
    int OldFiles,
    int SkippedEntries,
    bool Truncated,
    bool IsPersonalData,
    string Recommendation);

public sealed record DiskFinding(string Name, long CapacityBytes, long FreeBytes);

public sealed record ScanSnapshot(
    DateTimeOffset FinishedAt,
    IReadOnlyList<InstalledApp> Applications,
    IReadOnlyList<DependencyEvidence> Dependencies,
    IReadOnlyList<FolderFinding> Folders,
    IReadOnlyList<DiskFinding> Disks,
    IReadOnlyList<string> Warnings)
{
    public int ProtectedAppCount => Applications.Count(a => a.IsProtected);
    public int ObservedDependencyCount => Dependencies.Count;
}
