namespace SmartClean.Core;

// Locates only SupaClean's own Inno Setup uninstaller. This does not execute
// arbitrary registry uninstall strings or search unrelated directories.
public static class SelfUninstallLocator
{
    public static string? Find(string applicationBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationBaseDirectory)) return null;
        try
        {
            var root = Path.GetFullPath(applicationBaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) return null;

            // v0.4.0.12+ uses a fixed Uninstall subfolder. The root fallback is
            // retained so users upgrading from earlier Inno installs can still
            // remove SupaClean without reinstalling first.
            foreach (var candidate in new[]
            {
                Path.Combine(root, "Uninstall", "unins000.exe"),
                Path.Combine(root, "unins000.exe")
            })
            {
                var full = Path.GetFullPath(candidate);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var file = new FileInfo(full);
                if (!file.Exists) continue;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!file.Name.Equals("unins000.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                return full;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            return null;
        }
        return null;
    }
}
