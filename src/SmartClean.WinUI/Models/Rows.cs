using SmartClean.Core.Cleanup;
using SmartClean.Core;

namespace SmartClean.WinUI.Models;

public sealed class AppRow
{
    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Subtitle => $"{App.Publisher}  ·  {App.Version}";
    public string Size => Formatting.Bytes(App.EstimatedSizeBytes);
    public string Badge => App.IsProtected ? "Protected" : "Manual review";
    public string BadgeDescription => App.IsProtected ? "Shared/system safeguard" : "Never auto-removable";
    public AppRow(InstalledApp app) => App = app;
}

public sealed class FolderRow
{
    public FolderFinding Finding { get; }
    public string Name => Finding.Label;
    public string Path => Finding.Path;
    public string Size => Formatting.Bytes(Finding.Bytes);
    public string Detail => $"{Finding.Files:N0} files · {Finding.OldFiles:N0} modified over 180 days ago";
    public string Flag => Finding.Truncated || Finding.Issues.Any(i => !i.IsExpected)
        ? "Partial · read-only" : Finding.Label.StartsWith("Drive ", StringComparison.Ordinal)
            ? "Read-only drive inventory" : Finding.IsPersonalData ? "Personal files" : "Review only";
    public FolderRow(FolderFinding finding) => Finding = finding;
}

public sealed class TempCandidateRow
{
    public TempCandidate Candidate { get; }
    public string Name => System.IO.Path.GetFileName(Candidate.FullPath);
    public string Location => Candidate.FullPath;
    public string Detail => $"Modified {Candidate.LastWriteUtc.ToLocalTime():MMM d, yyyy} · Top-level temporary file";
    public string Size => Formatting.Bytes(Candidate.Bytes);
    public TempCandidateRow(TempCandidate candidate) => Candidate = candidate;
}

public sealed class TempExcludedRow
{
    public TempExcludedFile File { get; }
    public string Name => System.IO.Path.GetFileName(File.FullPath);
    public string Location => File.FullPath;
    public string Reason => File.Reason;
    public string Detail => File.LastWriteUtc == DateTime.MinValue
        ? "Metadata unavailable"
        : $"Modified {File.LastWriteUtc.ToLocalTime():MMM d, yyyy}";
    public string Size => Formatting.Bytes(File.Bytes);
    public TempExcludedRow(TempExcludedFile file) => File = file;
}

public sealed class RecoveryRow
{
    public RecoveryItem Item { get; }
    public string Name => System.IO.Path.GetFileName(Item.OriginalPath);
    public string Detail => $"Moved {Item.MovedAtUtc.LocalDateTime:MMM d, yyyy · h:mm tt} · {Formatting.Bytes(Item.Bytes)}";
    public string OriginalPath => Item.OriginalPath;
    public RecoveryRow(RecoveryItem item) => Item = item;
}

public sealed class RuntimeUsageRow
{
    public RuntimeUsage Usage { get; }
    public string Name => Usage.RuntimeName;
    public string Framework => Usage.FrameworkName + " · installed " + Usage.InstalledVersion;
    public string Summary => Usage.DeclaredDependents.Count == 0
        ? "No matching declarations observed — keep protected"
        : Usage.DeclaredDependents.Count + " app declaration(s) in the matching runtime family";
    public string Details => Usage.DeclaredDependents.Count == 0
        ? "This limited scan did not find an app declaring this runtime. That is NOT evidence it is unused."
        : string.Join("\n", Usage.DeclaredDependents.Select(d =>
            "• " + d.AppName + " (requests " + d.RequestedVersion + ")\n  Evidence: " + d.EvidencePath));
    public RuntimeUsageRow(RuntimeUsage usage) => Usage = usage;
}

public sealed class SharedComponentRow
{
    public SharedComponentUsage Usage { get; }
    public string Name => Usage.Name;
    public string Family => Usage.Category + " · " + Usage.Architecture
        + (Usage.Registered ? " · installed " + Usage.InstalledVersion : " · installation unverified");
    public string Summary => Usage.Evidence.Count == 0
        ? "No observed references — keep protected"
        : Usage.Evidence.Select(x => x.AppId).Distinct().Count()
            + " app(s) with evidence — review details";
    public string Details => Usage.Evidence.Count == 0
        ? "No matching imports or package declarations were observed in this bounded scan. "
          + "This does not mean the component is unused or safe to uninstall."
        : string.Join("\n\n", Usage.Evidence.Take(35).Select(e =>
            "• " + e.AppName + " — " + e.EvidenceType + " (" + e.Architecture + ")\n  "
            + e.Detail + "\n  Evidence: " + e.EvidencePath))
          + (Usage.Evidence.Count > 35 ? "\nAdditional evidence omitted from the preview." : "");
    public SharedComponentRow(SharedComponentUsage usage) => Usage = usage;
}
