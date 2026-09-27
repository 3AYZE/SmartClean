using System.Text.Json;
using SmartClean.Core;
using SmartClean.Core.Updates;

var failures = new List<string>();
void Check(bool ok, string test)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {test}");
    if (!ok) failures.Add(test);
}

var runtimeNames = new[]
{
    "Microsoft .NET Runtime - 10.0.0 (x64)",
    "Microsoft Windows Desktop Runtime - 10.0.0 (x64)",
    "Microsoft ASP.NET Core Runtime 10.0.0",
    "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
    "Microsoft Edge WebView2 Runtime",
    "Vulkan Runtime Libraries"
};
foreach (var name in runtimeNames)
    Check(ProtectionRules.Classify(name, "Microsoft Corporation").IsProtected,
        $"Shared runtime protected: {name}");
Check(!ProtectionRules.Classify("Old Video Editor", "Example Inc").IsProtected,
    "Ordinary app not falsely labeled a known runtime");
Check(ProtectionRules.Classify(null, null).IsProtected,
    "Missing app name treated as protected");

using (var json = JsonDocument.Parse("""
    {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
    """))
{
    var evidence = DependencyInspector.ParseRuntimeConfig("app-1", "Example", "test.runtimeconfig.json", json.RootElement);
    Check(evidence.Count == 1 && evidence[0].FrameworkName == "Microsoft.NETCore.App"
          && evidence[0].RequestedVersion == "10.0.0", "Explicit framework declaration extracted");
}
using (var json = JsonDocument.Parse("""
    {"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},
      {"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"}]}}
    """))
    Check(DependencyInspector.ParseRuntimeConfig("app-1", "Example", "test", json.RootElement).Count == 2,
        "Multiple runtime dependencies extracted");
using (var json = JsonDocument.Parse("""
    {"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}],
      "framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
    """))
    Check(DependencyInspector.ParseRuntimeConfig("app-1", "Example", "test", json.RootElement).Count == 0,
        "Self-contained runtimes not counted as external dependencies");

var scratch = Path.Combine(Path.GetTempPath(), "smartclean-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    Directory.CreateDirectory(Path.Combine(scratch, "nested"));
    var recent = Path.Combine(scratch, "nested", "recent.txt");
    var old = Path.Combine(scratch, "old.txt");
    File.WriteAllText(recent, "hello");
    File.WriteAllText(old, "older");
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-190));
    var scanner = new FolderScanner();
    var root = new FolderTarget("Test", scratch, IsPersonalData: true);
    var result = scanner.Scan([root], nowUtc: DateTime.UtcNow)[0];
    Check(result.Files == 2 && result.Bytes == 10 && result.OldFiles == 1,
        "Counts file sizes and age without reading file contents");
    Check(result.IsPersonalData && result.Recommendation.Contains("Inspect individually"),
        "Personal files never classified as automatically removable");
    var limited = scanner.Scan([root], maxEntriesPerRoot: 1)[0];
    Check(limited.Truncated, "Entry budget produces explicit partial-scan flag");
    var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try
    {
        scanner.Scan([root], canceled.Token);
        Check(false, "Cancellation interrupts scan");
    }
    catch (OperationCanceledException) { Check(true, "Cancellation interrupts scan"); }

    // A symlink must not allow the scan to wander into a different directory.
    var external = Path.Combine(Path.GetTempPath(), "smartclean-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(external);
    try
    {
        File.WriteAllBytes(Path.Combine(external, "hidden.bin"), new byte[9999]);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(scratch, "linked"), external);
            var linkedResult = scanner.Scan([root])[0];
            Check(linkedResult.Bytes == 10 && linkedResult.SkippedEntries >= 1,
                "Directory symlink is skipped; outside bytes not counted");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("SKIP  Symlink creation unavailable on this machine");
        }
    }
    finally { Directory.Delete(external, recursive: true); }
}
finally { Directory.Delete(scratch, recursive: true); }

Check(Formatting.Bytes(null) == "Not reported", "Missing app size is not invented");
Check(Formatting.Bytes(1024) == "1 KB", "Byte format correct");

// Stable release metadata is accepted only for our exact GitHub installer with a SHA-256 digest.
string updateFixture = """
    {"tag_name":"v0.2.0.3","draft":false,"prerelease":false,"assets":[
     {"name":"SmartClean-Setup.exe","size":12345678,
      "digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "browser_download_url":"https://github.com/3AYZE/SmartClean/releases/download/v0.2.0.3/SmartClean-Setup.exe"}]}
    """;
using (var fixture = JsonDocument.Parse(updateFixture))
{
    var release = ReleaseClient.ParseLatestRelease(fixture.RootElement);
    Check(release.Version == new Version(0, 2, 0, 3) && release.ByteCount == 12345678,
        "Verified public release metadata accepted");
}
void ShouldRejectUpdate(string json, string reason)
{
    try { using var fixture = JsonDocument.Parse(json); ReleaseClient.ParseLatestRelease(fixture.RootElement);
        Check(false, reason); }
    catch (UpdateException) { Check(true, reason); }
}
ShouldRejectUpdate(updateFixture.Replace("github.com/3AYZE/SmartClean", "other.example/3AYZE/SmartClean"),
    "Non-GitHub update URL rejected");
ShouldRejectUpdate(updateFixture.Replace("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:bad"),
    "Unverified update digest rejected");
ShouldRejectUpdate(updateFixture.Replace("v0.2.0.3", "v0.2.0.3-beta"),
    "Unstable release version rejected");
ShouldRejectUpdate(updateFixture.Replace("\"prerelease\":false", "\"prerelease\":true"),
    "Prerelease not installed via stable channel");

// A policy-level guard: the core library intentionally has no delete/uninstall public API.
var publicMethods = typeof(ScanCoordinator).Assembly.GetExportedTypes()
    .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public
                                 | System.Reflection.BindingFlags.Static
                                 | System.Reflection.BindingFlags.Instance
                                 | System.Reflection.BindingFlags.DeclaredOnly))
    // Record properties such as get_UninstallCommand are data, not destructive actions.
    .Where(m => !m.IsSpecialName)
    .Select(m => m.Name).ToArray();
Check(!publicMethods.Any(m => m.Contains("Delete", StringComparison.OrdinalIgnoreCase)
    || m.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
    || m.Contains("RemoveFile", StringComparison.OrdinalIgnoreCase)),
    "Public core API has no destructive operations");

Console.WriteLine($"\n{(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED")}");
return failures.Count == 0 ? 0 : 1;
