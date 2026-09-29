using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace SmartClean.Core;

// Services are part of the system relationship inventory, not proof of a
// framework/runtime dependency. Only registered executable paths are linked.
public sealed record ServiceRelationship(
    string ServiceName, string DisplayName, string ExecutablePath,
    string? AssociatedAppId, string? AssociatedAppName,
    bool ExecutableUnderWindows, string StartMode);

public sealed record ServiceInspection(
    IReadOnlyList<ServiceRelationship> Services, int Scanned, int Unreadable);

public sealed class ServiceRelationshipInspector
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services";
    private const int MaxServices = 3000;
    private static readonly Regex ExecutablePattern = new(
        @"(?i)^(.+?\.(?:exe|sys|dll))(?=\s|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public ServiceInspection Scan(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return new([], 0, 0);
        var found = new List<ServiceRelationship>();
        var unreadable = 0;
        var inspected = 0;
        var roots = apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallLocation))
            .Select(a =>
            {
                try
                {
                    var full = Path.GetFullPath(a.InstallLocation!);
                    return (App: a, Root: full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                }
                catch (Exception e) when (e is ArgumentException or IOException
                    or NotSupportedException or PathTooLongException)
                { return (App: a, Root: ""); }
            })
            .Where(x => x.Root.Length > 1)
            .OrderByDescending(x => x.Root.Length)
            .ToArray();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: false);
            if (root is null) return new([], 0, 0);
            foreach (var keyName in root.GetSubKeyNames().Take(MaxServices))
            {
                ct.ThrowIfCancellationRequested();
                inspected++;
                try
                {
                    using var key = root.OpenSubKey(keyName, writable: false);
                    if (key is null) { unreadable++; continue; }
                    var raw = key.GetValue("ImagePath") as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var path = ParseExecutablePath(raw);
                    if (path is null) continue;
                    var owner = roots.FirstOrDefault(x => path.StartsWith(
                        x.Root, StringComparison.OrdinalIgnoreCase));
                    var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var inWindowsDir = path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
                    var mode = key.GetValue("Start") switch
                    {
                        0 => "Boot",
                        1 => "System",
                        2 => "Automatic",
                        3 => "Demand",
                        4 => "Disabled",
                        _ => "Unknown"
                    };
                    found.Add(new ServiceRelationship(keyName,
                        key.GetValue("DisplayName") as string ?? keyName, path,
                        owner.App?.Id, owner.App?.Name, inWindowsDir, mode));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException
                    or RegexMatchTimeoutException)
                { unreadable++; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        { unreadable++; }
        return new(found.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray(), inspected, unreadable);
    }

    // No shell expansion, Process.Start or executable execution.
    // Registry ImagePath may include arguments, quotes, or environment variables.
    public static string? ParseExecutablePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        string binary;
        if (expanded.StartsWith('"'))
        {
            var close = expanded.IndexOf('"', 1);
            if (close <= 1) return null;
            binary = expanded.Substring(1, close - 1);
        }
        else
        {
            var match = ExecutablePattern.Match(expanded);
            if (!match.Success) return null;
            binary = match.Groups[1].Value;
        }
        try
        {
            if (binary.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    binary[@"\SystemRoot\".Length..]);
            return Path.IsPathFullyQualified(binary) ? Path.GetFullPath(binary) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
            or PathTooLongException) { return null; }
    }
}
