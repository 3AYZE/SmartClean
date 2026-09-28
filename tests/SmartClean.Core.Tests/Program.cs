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
// Inverse dependency relationships: a .NET 9 installed runtime family can
// report which REGISTERED apps declare that major/minor family. This is NOT
// evidence of exact patch loading or proof that unobserved runtimes are unused.
var fakeRuntimes = new[]
{
    new InstalledApp("runtime9", "Microsoft .NET Runtime - 9.0.12 (x64)",
        "Microsoft Corporation", "9.0.12", null, null, null, "test-registry", true,
        "Shared runtime", null),
    new InstalledApp("desktop9", "Microsoft Windows Desktop Runtime - 9.0.12 (x64)",
        "Microsoft Corporation", "9.0.12", null, null, null, "test-registry", true,
        "Shared runtime", null),
    new InstalledApp("runtime8", "Microsoft .NET Runtime - 8.0.14 (x64)",
        "Microsoft Corporation", "8.0.14", null, null, null, "test-registry", true,
        "Shared runtime", null),
    new InstalledApp("ordinary", "Video Editor", "Demo Inc", "1.0",
        @"D:\\VideoEditor", null, null, "test-registry", false, "Manual review", null)
};
var declarations = new[]
{
    new DependencyEvidence("ordinary", "Video Editor", "Microsoft.NETCore.App",
        "9.0.0", @"D:\\VideoEditor\\VideoEditor.runtimeconfig.json"),
    new DependencyEvidence("ordinary", "Video Editor", "Microsoft.WindowsDesktop.App",
        "9.0.0", @"D:\\VideoEditor\\VideoEditor.runtimeconfig.json")
};
var linkedRuntimes = RuntimeUsageAnalyzer.Build(fakeRuntimes, declarations);
Check(linkedRuntimes.Count == 3, "Only recognized installed runtime families are mapped");
Check(linkedRuntimes.Single(r => r.RuntimeAppId == "runtime9")
    .DeclaredDependents.Single().AppName == "Video Editor"
    && linkedRuntimes.Single(r => r.RuntimeAppId == "runtime9")
        .DeclaredDependents.Single().RequestedVersion == "9.0.0",
    ".NET 9 correctly shows an app declaring the 9.0 runtime family");
Check(linkedRuntimes.Single(r => r.RuntimeAppId == "desktop9")
    .DeclaredDependents.Single().AppName == "Video Editor",
    ".NET 9 desktop dependencies use the correct separate framework family");
Check(linkedRuntimes.Single(r => r.RuntimeAppId == "runtime8")
    .DeclaredDependents.Count == 0
    && fakeRuntimes.Single(r => r.Id == "runtime8").IsProtected,
    "No detected declaration never unprotects another installed runtime");
Check(RuntimeUsageAnalyzer.FrameworkFromInstalledName(
    "Microsoft .NET Framework 4.8") is null,
    "Legacy .NET Framework is never confused with the modern .NET 9 runtime");

var selectedTargets = DriveScanCatalog.SelectFixedRoots(
    [@"C:\\", @"D:\\"], [@"D:\\", @"C:\\", @"D:\\"]);
Check(selectedTargets.Count == 2
    && selectedTargets[0].Path == Path.GetPathRoot(Path.GetFullPath(@"D:\\"))
    && selectedTargets.All(t => t.IsPersonalData),
    "C and D or All fixed drives are deduplicated and always read-only inventory");
try
{
    DriveScanCatalog.SelectFixedRoots([@"C:\\", @"D:\\"], [@"E:\\"]);
    Check(false, "Unapproved drive root rejected");
}
catch (ArgumentException) { Check(true, "Unapproved drive root rejected"); }
try
{
    DriveScanCatalog.SelectFixedRoots([@"C:\\", @"D:\\"], [@"C:\\Windows"]);
    Check(false, "Folder path cannot masquerade as an allowed drive root");
}
catch (ArgumentException) { Check(true, "Folder path cannot masquerade as an allowed drive root"); }

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

// Other shared components must never be treated as removable on a missing match.
// Verify native import classification and a real on-disk, non-executed PE fixture.
Check(ComponentInspector.ClassifyImport("vcruntime140.dll")?.Family == "vc14"
      && ComponentInspector.ClassifyImport("msvcp120.dll")?.Family == "vc12",
    "Visual C++ native DLLs map to separate redistributable families");
Check(ComponentInspector.ClassifyImport("WebView2Loader.dll")?.Family == "webview2"
      && ComponentInspector.ClassifyImport("vulkan-1.dll")?.Family == "vulkan"
      && ComponentInspector.ClassifyImport("d3dx9_43.dll")?.Family == "directx-legacy",
    "WebView2, Vulkan and legacy DirectX imports are recognized");
Check(ComponentInspector.ClassifyImport("jvm.dll")?.Family == "java"
      && ComponentInspector.ClassifyImport("python312.dll")?.Family == "python3.12"
      && ComponentInspector.ClassifyImport("kernel32.dll") is null,
    "Java and Python imports are categorized without classifying ordinary Windows DLLs");
Check(SharedComponentAnalyzer.RecognizeInstalled(
        "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.0")?.Architecture == "x64"
      && SharedComponentAnalyzer.RecognizeInstalled(
        "Microsoft Edge WebView2 Runtime")?.Family == "webview2"
      && SharedComponentAnalyzer.RecognizeInstalled(
        "Python 3.12.5 (64-bit)")?.Family == "python3.12",
    "Recognized installed component families include architecture and interpreter versions");

var componentFixture = Path.Combine(Path.GetTempPath(),
    "smartclean-component-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(componentFixture);
try
{
    var exe = Path.Combine(componentFixture, "demo.exe");
    var bytes = new byte[1024];
    // Minimal valid PE32 header with one .text section and an import descriptor
    // referencing vcruntime140.dll. This fixture is NEVER executed.
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
        bytes.AsSpan(0, 2), 0x5A4D);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
        bytes.AsSpan(0x3c, 4), 0x80);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x80, 4), 0x00004550);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
        bytes.AsSpan(0x84, 2), 0x14c); // x86
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
        bytes.AsSpan(0x86, 2), 1); // one section
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
        bytes.AsSpan(0x94, 2), 0xE0); // PE32 optional header size
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
        bytes.AsSpan(0x98, 2), 0x10b); // PE32
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x98 + 104, 4), 0x1000); // import directory RVA
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x178 + 12, 4), 0x1000); // section RVA
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x178 + 16, 4), 0x200); // raw size
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x178 + 20, 4), 0x200); // raw offset
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(0x200 + 12, 4), 0x1040); // descriptor name RVA
    System.Text.Encoding.ASCII.GetBytes("vcruntime140.dll\0").CopyTo(bytes, 0x240);
    File.WriteAllBytes(exe, bytes);

    var depsFile = Path.Combine(componentFixture, "demo.deps.json");
    File.WriteAllText(depsFile, """
        {"libraries":{
          "Microsoft.Web.WebView2/1.0.3100":{"type":"package"},
          "Microsoft.WindowsAppSDK/1.7.0":{"type":"package"}
        }}
        """);
    var demo = new InstalledApp("demo", "Demo App", "Example",
        "1", componentFixture, 1024, null, "fixture-registry", false,
        "Manual review", null);
    var result = new ComponentInspector().Inspect([demo]);
    Check(result.BinariesRead == 1 && result.Evidence.Any(e =>
        e.Family == "vc14" && e.Architecture == "x86"
        && e.EvidencePath.EndsWith("demo.exe", StringComparison.OrdinalIgnoreCase)),
        "Bounded PE import parser detects x86 Visual C++ DLL with source file");
    Check(result.Evidence.Any(e => e.Family == "webview2"
        && e.EvidenceType == "Managed package declaration")
        && result.Evidence.Any(e => e.Family == "windows-app-sdk"),
        "Managed package declarations surface WebView2 and Windows App SDK usage");

    InstalledApp Runtime(string id, string name, string version) =>
        new(id, name, "Microsoft", version, null, null, null,
            "fixture-registry", true, "Shared runtime", null);
    var components = SharedComponentAnalyzer.Build(
        [
            Runtime("vc-x86", "Microsoft Visual C++ 2015-2022 Redistributable (x86)", "14.3"),
            Runtime("vc-x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.3"),
            Runtime("web", "Microsoft Edge WebView2 Runtime", "1.0"),
            Runtime("java", "Java 8 Update 421", "8.0")
        ], result.Evidence);
    Check(components.Single(c => c.Id == "vc-x86").Evidence.Any(e => e.AppName == "Demo App")
        && components.Single(c => c.Id == "vc-x64").Evidence.Count == 0,
        "Native x86 imports do not incorrectly implicate the x64 redistributable");
    Check(components.Single(c => c.Id == "web").Evidence.Any(e => e.AppId == "demo"),
        "Declared WebView2 package appears under recognized installed runtime");
    Check(components.Single(c => c.Id == "java").Evidence.Count == 0,
        "No evidence for a registered Java runtime does not infer uninstall safety");
}
finally
{
    if (Directory.Exists(componentFixture))
        Directory.Delete(componentFixture, recursive: true);
}

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
            Check(linkedResult.Issues.Any(i => i.IsExpected
                && i.Path.EndsWith("linked", StringComparison.OrdinalIgnoreCase)),
                "Expected linked-path exclusions carry the actual path and do not imply scan failure");
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
    Check(found.InspectedEntries == 3 && found.Skipped == 2
        && found.ExcludedFiles.Count == 2,
        "Excluded top-level files remain visible in read-only inspection with no subfolder traversal");
    Check(found.ExclusionReasons.TryGetValue("Modified within the last 30 days", out var recentExclusions)
        && recentExclusions == 1
        && found.ExclusionReasons.TryGetValue(
            "Not a supported temporary-file type (.tmp, .temp, .log, .dmp)", out var unsupportedExclusions)
        && unsupportedExclusions == 1,
        "Excluded files include specific reasons rather than one generic skipped count");
    Check(found.ExcludedFiles.All(x => x.FullPath != nestedFile),
        "Nested application data stays outside the deletion and excluded-file previews");

    var candidate = found.Items[0];
    File.AppendAllText(aged, "changed");
    try { cleaner.MoveToRecovery(candidate); Check(false, "Changed candidate blocked"); }
    catch (InvalidOperationException) { Check(true, "Changed candidate blocked"); }

    // The modified file is now recent; make it old again for the recovery lifecycle fixture.
    File.SetLastWriteTimeUtc(aged, oldDate);
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

    var oldLog = Path.Combine(tempFixture, "diagnostic.log");
    var oldDump = Path.Combine(tempFixture, "old-crash.dmp");
    File.WriteAllText(oldLog, "test log");
    File.WriteAllText(oldDump, "test minidump");
    File.SetLastWriteTimeUtc(oldLog, oldDate);
    File.SetLastWriteTimeUtc(oldDump, oldDate);
    var expanded = cleaner.FindCandidates();
    Check(expanded.Items.Count == 2
        && expanded.Items.Any(x => x.FullPath == oldLog)
        && expanded.Items.Any(x => x.FullPath == oldDump),
        "Reviewed 30-day-old top-level log and dump files can appear on the selection list");
    var reviewedLog = expanded.Items.Single(x => x.FullPath == oldLog);
    var storedLog = cleaner.MoveToRecovery(reviewedLog);
    Check(cleaner.ListRecovery().Single().Id == storedLog.Id,
        "Only a selected, revalidated log file moves to Recovery");
    cleaner.Restore(storedLog.Id);
    Check(File.Exists(oldLog) && cleaner.ListRecovery().Count == 0,
        "Reviewed log file restores without requiring irreversible deletion");
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
