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
    public string Flag => Finding.Truncated ? "Partial scan" : Finding.IsPersonalData ? "Personal files" : "Review only";
    public FolderRow(FolderFinding finding) => Finding = finding;
}
