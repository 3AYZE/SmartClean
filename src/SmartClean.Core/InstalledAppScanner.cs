using Microsoft.Win32;
using System.Globalization;

namespace SmartClean.Core;

public sealed class InstalledAppScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> Scan(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<InstalledApp>();
        var found = new List<InstalledApp>();
        RegistryView[] views = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var subname in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var entry = uninstall.OpenSubKey(subname);
                        if (entry is null) continue;
                        var name = Read(entry, "DisplayName");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        // Hidden shared runtimes such as WebView2 are inventory only.
                        // Keep other hidden system components out of the app list.
                        if (IsTrue(entry.GetValue("SystemComponent"))
                            && SharedComponentAnalyzer.RecognizeInstalled(name) is null) continue;
                        // Update/patch records are not standalone apps and often exaggerate storage.
                        if (Read(entry, "ReleaseType") is not null || Read(entry, "ParentKeyName") is not null) continue;
                        var publisher = Read(entry, "Publisher") ?? "Unknown publisher";
                        var (protect, reason) = ProtectionRules.Classify(name, publisher);
                        found.Add(new InstalledApp(
                            Id: $"{hive}/{view}/{subname}",
                            Name: name.Trim(),
                            Publisher: publisher,
                            Version: Read(entry, "DisplayVersion") ?? "—",
                            InstallLocation: CleanPath(Read(entry, "InstallLocation")),
                            EstimatedSizeBytes: ReadEstimatedBytes(entry),
                            InstalledOn: ParseInstallDate(Read(entry, "InstallDate")),
                            RegistryLocation: $@"{hive}\{UninstallPath}\{subname} [{view}]",
                            IsProtected: protect,
                            ProtectionReason: reason,
                            UninstallCommand: Read(entry, "UninstallString")));
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
                    {
                        // One inaccessible or damaged registry entry must not fail an entire scan.
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Some per-machine locations may not be accessible to a standard user.
            }
        }

        // Windows registry views can expose the same installed program twice.
        // Dedup only matching name+publisher+version+install location; retain distinct side-by-side installs.
        return found.GroupBy(a => string.Join("|", a.Name, a.Publisher, a.Version,
                    a.InstallLocation ?? ""), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.EstimatedSizeBytes ?? 0).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool IsTrue(object? value) => value switch
    {
        int i => i != 0,
        long l => l != 0,
        string s => s == "1",
        _ => false
    };

    private static string? Read(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as string; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static long? ReadEstimatedBytes(RegistryKey entry)
    {
        try
        {
            var value = entry.GetValue("EstimatedSize"); // Registry value is KB, not bytes.
            var kb = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return kb is > 0 and < 2_000_000_000 ? checked(kb * 1024) : null;
        }
        catch (Exception e) when (e is OverflowException or FormatException or InvalidCastException)
        { return null; }
    }

    private static DateTime? ParseInstallDate(string? date) =>
        DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var result) && result <= DateTime.Today ? result : null;
}
