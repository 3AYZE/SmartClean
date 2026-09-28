using System.Text.RegularExpressions;

namespace SmartClean.Core;

// This describes manifest declarations, not proof that a particular process
// actually loaded the installed patch version. Absence is never uninstall evidence.
public sealed record RuntimeDependent(
    string AppId, string AppName, string RequestedVersion, string EvidencePath);

public sealed record RuntimeUsage(
    string RuntimeAppId, string RuntimeName, string InstalledVersion,
    string FrameworkName, IReadOnlyList<RuntimeDependent> DeclaredDependents);

public static class RuntimeUsageAnalyzer
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<RuntimeUsage> Build(
        IReadOnlyList<InstalledApp> apps, IReadOnlyList<DependencyEvidence> evidence)
    {
        var output = new List<RuntimeUsage>();
        foreach (var runtime in apps)
        {
            var framework = FrameworkFromInstalledName(runtime.Name);
            if (framework is null) continue;
            var installedVersion = ExtractVersion(runtime.Version)
                ?? ExtractVersion(runtime.Name);
            if (installedVersion is null) continue;
            var dependents = evidence
                .Where(e => e.FrameworkName.Equals(framework, StringComparison.OrdinalIgnoreCase)
                    && RequestedFamilyMatches(e.RequestedVersion, installedVersion))
                .GroupBy(e => (e.AppId, e.RequestedVersion))
                .Select(group =>
                {
                    var e = group.First();
                    return new RuntimeDependent(e.AppId, e.AppName,
                        e.RequestedVersion, e.EvidencePath);
                })
                .OrderBy(e => e.AppName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.RequestedVersion, StringComparer.Ordinal)
                .ToArray();
            output.Add(new RuntimeUsage(runtime.Id, runtime.Name,
                installedVersion.ToString(), framework, dependents));
        }
        return output.OrderBy(r => r.RuntimeName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string? FrameworkFromInstalledName(string installedName)
    {
        if (string.IsNullOrWhiteSpace(installedName)) return null;
        var s = installedName.Trim();
        if (Regex.IsMatch(s, @"^Microsoft\s+\.NET\s+Runtime\s*[- ]\s*\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Microsoft.NETCore.App";
        if (Regex.IsMatch(s, @"^Microsoft\s+(?:(?:Windows\s+Desktop)|(?:\.NET\s+Desktop))\s+Runtime\s*[- ]\s*\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Microsoft.WindowsDesktop.App";
        if (Regex.IsMatch(s, @"^Microsoft\s+ASP\.NET\s+Core\s+Runtime\s*[- ]\s*\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Microsoft.AspNetCore.App";
        return null;
    }

    private static Version? ExtractVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = VersionPattern.Match(text);
        return match.Success && Version.TryParse(match.Value, out var version)
            ? version : null;
    }

    // Same major+minor is a *possible framework family* match. We deliberately
    // do not claim the exact installed patch is the runtime the app loaded.
    private static bool RequestedFamilyMatches(string requested, Version installed)
    {
        var version = ExtractVersion(requested);
        return version is not null && version.Major == installed.Major
            && version.Minor == installed.Minor;
    }
}
