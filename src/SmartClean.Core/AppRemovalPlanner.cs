using System.Text.RegularExpressions;

namespace SmartClean.Core;

public enum AppRemovalMethod
{
    Protected,
    WindowsInstaller,
    WindowsSettings
}

public sealed record AppRemovalPlan(
    AppRemovalMethod Method,
    string Summary,
    string? ProductCode,
    bool CanDirectUninstall);

public static class AppRemovalPlanner
{
    private static readonly Regex MsiCommandPattern = new(
        "^\\s*\\\"?(?:[^\\\"]*\\\\)?msiexec(?:\\.exe)?\\\"?\\s+/(?:I|X)\\s*(\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static AppRemovalPlan Plan(InstalledApp app)
    {
        if (app.IsProtected)
            return new(AppRemovalMethod.Protected,
                "Direct removal is blocked because this entry is a protected shared/system component. "
                + "Review its dependency evidence before changing it outside SupaClean.",
                null, false);

        if (TryGetMsiProductCode(app, out var productCode))
            return new(AppRemovalMethod.WindowsInstaller,
                "Windows Installer can uninstall this app using a validated MSI product code. "
                + "SupaClean will launch the standard MSI uninstall UI; you can still cancel there.",
                productCode, true);

        return new(AppRemovalMethod.WindowsSettings,
            "No validated MSI product code was found. SupaClean will not execute arbitrary uninstall commands "
            + "stored in the registry. Use Windows Installed apps for this program.",
            null, false);
    }

    public static bool TryGetMsiProductCode(InstalledApp app, out string? productCode)
    {
        productCode = null;

        // MSI uninstall registry keys are commonly the product-code GUID.
        var slash = app.Id.LastIndexOf('/');
        if (slash >= 0 && slash < app.Id.Length - 1)
        {
            var keyName = app.Id[(slash + 1)..].Trim();
            if (TryNormalizeGuid(keyName, out productCode)) return true;
        }

        // Reading an MSI GUID from an uninstall string is evidence only.
        // We NEVER execute the registry string. The UI launches msiexec.exe
        // itself with only /x and this validated GUID.
        var command = app.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command)) return false;

        try
        {
            var match = MsiCommandPattern.Match(command);
            return match.Success && TryNormalizeGuid(match.Groups[1].Value, out productCode);
        }
        catch (RegexMatchTimeoutException)
        {
            productCode = null;
            return false;
        }
    }

    private static bool TryNormalizeGuid(string candidate, out string? normalized)
    {
        normalized = null;
        if (!Guid.TryParse(candidate.Trim(), out var guid) || guid == Guid.Empty) return false;
        normalized = "{" + guid.ToString("D").ToUpperInvariant() + "}";
        return true;
    }
}
