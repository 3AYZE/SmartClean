using System.Text.Json;
using SmartClean.Core;
using SmartClean.Core.Updates;
using SmartClean.Core.Cleanup;

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

// A candidate is NOT permission to delete an arbitrary old file.
// Exercise the actual move/journal/restore/purge lifecycle in an isolated fixture.
var tempFixture = Path.Combine(Path.GetTempPath(),
    "smartclean-temp-test-" + Guid.NewGuid().ToString("N"));
var vaultFixture = tempFixture + "-vault";
Directory.CreateDirectory(tempFixture);
try
{
    var aged = Path.Combine(tempFixture, "aged.tmp");
    var newFile = Path.Combine(tempFixture, "fresh.tmp");
    var document = Path.Combine(tempFixture, "personal.docx");
    var nested = Path.Combine(tempFixture, "private");
    Directory.CreateDirectory(nested);
    var nestedFile = Path.Combine(nested, "nested.tmp");
    foreach (var file in new[] { aged, newFile, document, nestedFile })
        File.WriteAllText(file, "sample");
    var oldDate = DateTime.UtcNow.AddDays(-40);
    foreach (var file in new[] { aged, document, nestedFile })
        File.SetLastWriteTimeUtc(file, oldDate);

    var cleaner = new TempRecoveryManager(tempFixture, vaultFixture, testPaths: true);
    var found = cleaner.FindCandidates();
    Check(found.Items.Count == 1 && found.Items[0].FullPath == aged,
        "Only old, top-level temporary files qualify; documents and subfolders protected");

    var candidate = found.Items[0];
    File.AppendAllText(aged, "changed");
    try { cleaner.MoveToRecovery(candidate); Check(false, "Changed candidate blocked"); }
    catch (InvalidOperationException) { Check(true, "Changed candidate blocked"); }

    var current = cleaner.FindCandidates().Items.Single();
    var saved = cleaner.MoveToRecovery(current);
    Check(!File.Exists(aged) && cleaner.ListRecovery().Single().Id == saved.Id,
        "Move journals original path and leaves recoverable payload");
    Check(File.Exists(Path.Combine(vaultFixture, saved.Id, "temporary-file.bin")),
        "Quarantine contains exact payload after move");

    File.WriteAllText(aged, "new file must survive");
    try { cleaner.Restore(saved.Id); Check(false, "Restore refuses overwrite"); }
    catch (IOException) { Check(true, "Restore refuses overwrite"); }
    Check(File.ReadAllText(aged) == "new file must survive",
        "Original-path collision is unchanged");
    File.Delete(aged);
    cleaner.Restore(saved.Id);
    Check(File.Exists(aged) && cleaner.ListRecovery().Count == 0,
        "Restore returns original file and removes it from active recovery list");

    var again = cleaner.FindCandidates().Items.Single();
    var againSaved = cleaner.MoveToRecovery(again);
    var purged = cleaner.PurgeFromRecovery(againSaved.Id);
    Check(purged > 0 && cleaner.ListRecovery().Count == 0
        && !File.Exists(Path.Combine(vaultFixture, againSaved.Id, "temporary-file.bin")),
        "Only explicitly quarantined payload can be purged");

    try
    {
        cleaner.PurgeFromRecovery("../invalid");
        Check(false, "Invalid recovery identifier blocked");
    }
    catch (ArgumentException) { Check(true, "Invalid recovery identifier blocked"); }

    Check(File.Exists(document) && File.Exists(newFile) && File.Exists(nestedFile),
        "Personal, recent and nested files remain intact");
}
finally
{
    if (Directory.Exists(tempFixture)) Directory.Delete(tempFixture, recursive: true);
    if (Directory.Exists(vaultFixture)) Directory.Delete(vaultFixture, recursive: true);
}

// Keep direct destructive APIs isolated to the narrowly scoped recovery engine.
var unsafeMethods = typeof(ScanCoordinator).Assembly.GetExportedTypes()
    .Where(t => t != typeof(TempRecoveryManager))
    .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public
                                 | System.Reflection.BindingFlags.Static
                                 | System.Reflection.BindingFlags.Instance
                                 | System.Reflection.BindingFlags.DeclaredOnly))
    .Where(m => !m.IsSpecialName)
    .Select(m => m.Name).ToArray();
Check(!unsafeMethods.Any(m => m.Contains("Delete", StringComparison.OrdinalIgnoreCase)
    || m.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
    || m.Contains("Purge", StringComparison.OrdinalIgnoreCase)),
    "No general-purpose delete or uninstall API exposed by core");

Console.WriteLine($"\n{(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED")}");
return failures.Count == 0 ? 0 : 1;
