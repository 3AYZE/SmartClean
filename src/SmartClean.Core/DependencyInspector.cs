using System.Text.Json;

namespace SmartClean.Core;

// V1 deliberately limits itself to explicit .NET runtimeconfig declarations.
// It never concludes that an app has NO dependencies.
public sealed class DependencyInspector
{
    public IReadOnlyList<DependencyEvidence> Inspect(
        IReadOnlyList<InstalledApp> apps,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DependencyEvidence>();
        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(app.InstallLocation)) continue;
            try
            {
                var root = new DirectoryInfo(app.InstallLocation);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var directories = new List<DirectoryInfo> { root };
                // Bounded discovery: no deep recursion into games, archives or user data.
                foreach (var child in root.EnumerateDirectories().Take(6))
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                        directories.Add(child);

                var inspected = 0;
                foreach (var dir in directories)
                {
                    foreach (var config in dir.EnumerateFiles("*.runtimeconfig.json").Take(8))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++inspected > 20) break;
                        try
                        {
                            if (config.Length > 1_048_576) continue;
                            using var stream = config.OpenRead();
                            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
                            { MaxDepth = 16 });
                            foreach (var dep in ParseRuntimeConfig(app.Id, app.Name, config.FullName, doc.RootElement))
                                results.Add(dep);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                            or JsonException or System.Security.SecurityException)
                        { /* This config is unreadable; leave dependencies unverified. */ }
                    }
                    if (inspected > 20) break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException or PathTooLongException)
            { /* Do not infer a negative dependency result from scan failure. */ }
        }
        return results.DistinctBy(d => (d.AppId, d.FrameworkName, d.RequestedVersion, d.EvidencePath)).ToArray();
    }

    public static IReadOnlyList<DependencyEvidence> ParseRuntimeConfig(
        string appId, string appName, string evidencePath, JsonElement json)
    {
        var output = new List<DependencyEvidence>();
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("runtimeOptions", out var options)
            || options.ValueKind != JsonValueKind.Object)
            return output;
        // includedFrameworks indicates a self-contained distribution: do not claim
        // those frameworks must exist as externally installed runtime packages.
        if (options.TryGetProperty("includedFrameworks", out var included)
            && included.ValueKind == JsonValueKind.Array)
            return output;

        if (options.TryGetProperty("framework", out var single)) Add(single);
        if (options.TryGetProperty("frameworks", out var multiple)
            && multiple.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in multiple.EnumerateArray()) Add(item);
        }
        return output;

        void Add(JsonElement framework)
        {
            if (framework.ValueKind != JsonValueKind.Object) return;
            if (!framework.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String) return;
            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name)) return;
            var version = framework.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString() ?? "unspecified" : "unspecified";
            output.Add(new DependencyEvidence(appId, appName, name, version, evidencePath));
        }
    }
}
