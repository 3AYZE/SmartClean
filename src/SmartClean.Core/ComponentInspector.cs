using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SmartClean.Core;

// Evidence from bounded, read-only inspection of registered installation folders.
// A native import or package declaration NEVER proves a shared installed package is needed:
// the DLL/runtime may be bundled with the application or loaded conditionally.
public sealed record ComponentEvidence(
    string AppId, string AppName, string Family, string Category,
    string EvidenceType, string Detail, string EvidencePath, string Architecture);

public sealed record ComponentInspection(
    IReadOnlyList<ComponentEvidence> Evidence, int BinariesRead,
    int UnreadableFiles, int UninspectedApps);

public sealed class ComponentInspector
{
    private const long MaxBinaryLength = 64L * 1024 * 1024;
    private const long MaxDepsLength = 2L * 1024 * 1024;
    private const int MaxBinaryPerApp = 12;
    private const int MaxDepsPerApp = 8;
    private const int MaxFoldersPerApp = 4;
    private const int MaxApps = 1200;

    public ComponentInspection Inspect(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default)
    {
        var evidence = new List<ComponentEvidence>();
        var binaries = 0;
        var unreadable = 0;
        var uninspected = 0;
        foreach (var app in apps.Take(MaxApps))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(app.InstallLocation)) { uninspected++; continue; }
            try
            {
                var root = new DirectoryInfo(app.InstallLocation);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
                { uninspected++; continue; }
                var dirs = new List<DirectoryInfo> { root };
                // No recursive crawl: a few immediate child folders and file limits only.
                foreach (var child in root.EnumerateDirectories().Take(MaxFoldersPerApp - 1))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0) dirs.Add(child);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException
                        or System.Security.SecurityException) { unreadable++; }
                }
                var binaryCount = 0;
                var depsCount = 0;
                foreach (var dir in dirs)
                {
                    foreach (var pattern in new[] { "*.exe", "*.dll" })
                    {
                        foreach (var binary in dir.EnumerateFiles(pattern).Take(8))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (binaryCount >= MaxBinaryPerApp) break;
                            binaryCount++;
                            try
                            {
                                if ((binary.Attributes & FileAttributes.ReparsePoint) != 0
                                    || binary.Length is <= 0 or > MaxBinaryLength) continue;
                                var imports = ReadNativeImports(binary.FullName, ct);
                                binaries++;
                                foreach (var entry in imports)
                                {
                                    var mapped = ClassifyImport(entry.Dll);
                                    if (mapped is null) continue;
                                    evidence.Add(new ComponentEvidence(app.Id, app.Name, mapped.Value.Family,
                                        mapped.Value.Category, "Native PE import",
                                        "Imports " + entry.Dll + " — may be bundled or resolved at runtime",
                                        binary.FullName, entry.Architecture));
                                }
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                or System.Security.SecurityException or BadImageFormatException
                                or ArgumentException or EndOfStreamException) { unreadable++; }
                        }
                    }
                    foreach (var deps in dir.EnumerateFiles("*.deps.json").Take(4))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++depsCount > MaxDepsPerApp) break;
                        try
                        {
                            if (deps.Length is <= 0 or > MaxDepsLength
                                || (deps.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            using var stream = deps.OpenRead();
                            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 20 });
                            if (!doc.RootElement.TryGetProperty("libraries", out var libs)
                                || libs.ValueKind != JsonValueKind.Object) continue;
                            foreach (var library in libs.EnumerateObject().Take(800))
                            {
                                if (library.Name.StartsWith("Microsoft.Web.WebView2/",
                                        StringComparison.OrdinalIgnoreCase))
                                    evidence.Add(new ComponentEvidence(app.Id, app.Name, "webview2",
                                        "WebView2", "Managed package declaration", library.Name,
                                        deps.FullName, "any"));
                                else if (library.Name.StartsWith("Microsoft.WindowsAppSDK/",
                                        StringComparison.OrdinalIgnoreCase))
                                    evidence.Add(new ComponentEvidence(app.Id, app.Name, "windows-app-sdk",
                                        "Windows App SDK", "Managed package declaration", library.Name,
                                        deps.FullName, "any"));
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                            or System.Security.SecurityException or JsonException) { unreadable++; }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException or PathTooLongException)
            { uninspected++; }
        }
        uninspected += Math.Max(0, apps.Count - MaxApps);
        return new ComponentInspection(evidence.DistinctBy(e =>
            (e.AppId, e.Family, e.Detail, e.EvidencePath, e.Architecture)).ToArray(),
            binaries, unreadable, uninspected);
    }

    // Broad family evidence only. It never identifies the exact redistributable installer.
    public static (string Family, string Category)? ClassifyImport(string dll)
    {
        dll = Path.GetFileName(dll).ToLowerInvariant();
        if (dll is "vcruntime140.dll" or "vcruntime140_1.dll" or "msvcp140.dll"
            or "msvcp140_1.dll" or "msvcp140_2.dll" or "concrt140.dll")
            return ("vc14", "Visual C++ 2015–2022");
        if (dll is "msvcr120.dll" or "msvcp120.dll") return ("vc12", "Visual C++ 2013");
        if (dll is "webview2loader.dll") return ("webview2", "WebView2");
        if (dll is "vulkan-1.dll") return ("vulkan", "Vulkan graphics");
        if (dll.StartsWith("d3dx9_", StringComparison.Ordinal)
            || dll.StartsWith("d3dx10_", StringComparison.Ordinal)
            || dll.StartsWith("d3dx11_", StringComparison.Ordinal)
            || dll.StartsWith("xinput1_3.dll", StringComparison.Ordinal))
            return ("directx-legacy", "Legacy DirectX components");
        if (dll is "microsoft.windowsappruntime.bootstrap.dll")
            return ("windows-app-sdk", "Windows App SDK");
        if (dll is "jvm.dll") return ("java", "Java VM");
        // Avoid treating Python 3.x embedded DLLs as proof that a specific
        // globally installed Python patch is in use.
        if (dll.StartsWith("python3", StringComparison.Ordinal)
            && dll.EndsWith(".dll", StringComparison.Ordinal))
        {
            var digits = dll[6..^4];
            if (digits.Length >= 2 && digits.All(char.IsAsciiDigit))
            {
                var minor = digits[1..];
                if (int.TryParse(minor, out var n) && n is >= 0 and <= 99)
                    return ($"python3.{n}", "Python " + 3 + "." + n);
            }
        }
        return null;
    }

    private sealed record ImportName(string Dll, string Architecture);

    // Parse the standard PE import descriptor table only. No executable code
    // is loaded, started or trusted. All offsets and counts are bounded.
    private static IReadOnlyList<ImportName> ReadNativeImports(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 512 || stream.Length > MaxBinaryLength) return [];
        if (reader.ReadUInt16() != 0x5a4d) return [];
        stream.Position = 0x3c;
        var pe = reader.ReadInt32();
        if (pe < 0x40 || pe > stream.Length - 260) return [];
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x00004550) return [];
        var machine = reader.ReadUInt16();
        var arch = machine switch { 0x14c => "x86", 0x8664 => "x64",
            0xaa64 => "arm64", _ => "unknown" };
        var nSections = reader.ReadUInt16();
        if (nSections is < 1 or > 96) return [];
        stream.Position = pe + 20;
        var optionalSize = reader.ReadUInt16();
        if (optionalSize is < 128 or > 4096
            || pe + 24L + optionalSize + nSections * 40L > stream.Length) return [];
        stream.Position = pe + 24;
        var optional = reader.ReadBytes(optionalSize);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        var importDirectoryOffset = magic switch { 0x10b => 104, 0x20b => 120, _ => -1 };
        if (importDirectoryOffset < 0 || optional.Length < importDirectoryOffset + 8) return [];
        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(
            optional.AsSpan(importDirectoryOffset, 4));
        if (importRva == 0) return [];
        stream.Position = pe + 24 + optionalSize;
        var sections = new List<(uint Rva, uint RawSize, uint RawOffset)>();
        for (var i = 0; i < nSections; i++)
        {
            var section = reader.ReadBytes(40);
            sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(20, 4))));
        }
        long? OffsetForRva(uint rva, int count)
        {
            foreach (var section in sections)
            {
                if (rva < section.Rva || rva - section.Rva > section.RawSize
                    || section.RawSize - (rva - section.Rva) < count) continue;
                var offset = (long)section.RawOffset + (rva - section.Rva);
                if (offset >= 0 && offset <= stream.Length - count) return offset;
            }
            return null;
        }

        var start = OffsetForRva(importRva, 20);
        if (start is null) return [];
        var results = new List<ImportName>();
        for (var i = 0; i < 128; i++)
        {
            ct.ThrowIfCancellationRequested();
            var descriptorOffset = start.Value + i * 20L;
            if (descriptorOffset > stream.Length - 20) break;
            stream.Position = descriptorOffset;
            var raw = reader.ReadBytes(20);
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
            if (nameRva == 0) break;
            var nameOffset = OffsetForRva(nameRva, 1);
            if (nameOffset is null) continue;
            stream.Position = nameOffset.Value;
            var bytes = new List<byte>(64);
            for (var j = 0; j < 128 && stream.Position < stream.Length; j++)
            {
                var b = reader.ReadByte();
                if (b == 0) break;
                if (b is < 32 or > 126) { bytes.Clear(); break; }
                bytes.Add(b);
            }
            if (bytes.Count is < 5 or > 100) continue;
            results.Add(new ImportName(Encoding.ASCII.GetString(bytes.ToArray()), arch));
        }
        return results;
    }
}
