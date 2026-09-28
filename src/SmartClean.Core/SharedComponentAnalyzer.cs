using System.Text.RegularExpressions;

namespace SmartClean.Core;

public sealed record SharedComponentIdentity(string Family, string Category, string Architecture);

// A relationship is evidence that the app references a DLL / managed package,
// NOT proof that the separately registered runtime package supplied it.
public sealed record SharedComponentUsage(
    string Id, string Name, string Category, string Family, string Architecture,
    string InstalledVersion, bool Registered, IReadOnlyList<ComponentEvidence> Evidence);

public static class SharedComponentAnalyzer
{
    private static readonly Regex PythonVersion = new(
        @"^Python\s+(3\.\d+)(?:\.\d+)?(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static SharedComponentIdentity? RecognizeInstalled(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.Trim();
        var arch = s.Contains("(x64)", StringComparison.OrdinalIgnoreCase)
            || s.Contains("64-bit", StringComparison.OrdinalIgnoreCase) ? "x64"
            : s.Contains("(x86)", StringComparison.OrdinalIgnoreCase)
            || s.Contains("32-bit", StringComparison.OrdinalIgnoreCase) ? "x86" : "any";
        if (s.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            && s.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Contains("2013", StringComparison.OrdinalIgnoreCase))
                return new("vc12", "Visual C++ 2013", arch);
            if (new[] { "2015", "2017", "2019", "2022" }
                .Any(year => s.Contains(year, StringComparison.OrdinalIgnoreCase)))
                return new("vc14", "Visual C++ 2015–2022", arch);
        }
        if (s.Contains("Edge WebView2 Runtime", StringComparison.OrdinalIgnoreCase))
            return new("webview2", "WebView2", arch);
        if (s.Contains("Windows App SDK Runtime", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Windows App Runtime", StringComparison.OrdinalIgnoreCase))
            return new("windows-app-sdk", "Windows App SDK", arch);
        if (s.Contains("Vulkan Run Time Libraries", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Vulkan Runtime", StringComparison.OrdinalIgnoreCase))
            return new("vulkan", "Vulkan graphics", arch);
        if (s.Contains("DirectX", StringComparison.OrdinalIgnoreCase)
            && (s.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || s.Contains("End-User", StringComparison.OrdinalIgnoreCase)))
            return new("directx-legacy", "Legacy DirectX components", arch);
        if (s.StartsWith("Java ", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Java(TM)", StringComparison.OrdinalIgnoreCase)
            || (s.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)
                && (s.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("JRE", StringComparison.OrdinalIgnoreCase)))
            || (s.Contains("Temurin", StringComparison.OrdinalIgnoreCase)
                && s.Contains("JRE", StringComparison.OrdinalIgnoreCase)))
            return new("java", "Java VM", arch);
        var python = PythonVersion.Match(s);
        if (python.Success)
            return new("python" + python.Groups[1].Value, "Python " + python.Groups[1].Value, arch);
        return null;
    }

    public static IReadOnlyList<SharedComponentUsage> Build(
        IReadOnlyList<InstalledApp> apps, IReadOnlyList<ComponentEvidence> evidence)
    {
        var output = new List<SharedComponentUsage>();
        var recognized = apps.Select(app => (App: app, Identity: RecognizeInstalled(app.Name)))
            .Where(x => x.Identity is not null).ToArray();
        foreach (var (app, maybeIdentity) in recognized)
        {
            var identity = maybeIdentity!;
            var related = evidence.Where(x =>
                x.AppId != app.Id
                && x.Family.Equals(identity.Family, StringComparison.OrdinalIgnoreCase)
                && (identity.Architecture == "any" || x.Architecture == "any"
                    || x.Architecture == identity.Architecture))
                .DistinctBy(x => (x.AppId, x.Family, x.EvidencePath, x.Detail))
                .OrderBy(x => x.AppName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            output.Add(new SharedComponentUsage(app.Id, app.Name, identity.Category,
                identity.Family, identity.Architecture, app.Version, true, related));
        }

        // Detect use even if no matching install is visible in uninstall records
        // (e.g. WebView2 can be registered as a hidden system component).
        foreach (var group in evidence.GroupBy(e => (e.Family, e.Architecture)))
        {
            if (recognized.Any(x =>
                x.Identity!.Family.Equals(group.Key.Family, StringComparison.OrdinalIgnoreCase)
                && (x.Identity.Architecture == "any" || group.Key.Architecture == "any"
                    || x.Identity.Architecture == group.Key.Architecture))) continue;
            var examples = group.DistinctBy(x => (x.AppId, x.EvidencePath, x.Detail))
                .OrderBy(x => x.AppName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            output.Add(new SharedComponentUsage("observed:" + group.Key.Family
                + ":" + group.Key.Architecture, group.First().Category + " (observed)",
                group.First().Category, group.Key.Family, group.Key.Architecture,
                "Not established", false, examples));
        }
        return output.OrderBy(x => x.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
