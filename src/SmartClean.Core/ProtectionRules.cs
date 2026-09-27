using System.Text.RegularExpressions;

namespace SmartClean.Core;

public static class ProtectionRules
{
    private static readonly (Regex Name, string Reason)[] Rules =
    [
        (NewPattern(@"\b(?:microsoft\s+)?(?:\.net\s+(?:framework|runtime|desktop\s+runtime)|windows\s+desktop\s+runtime|asp\.net\s+core\s+runtime)\b"), "Shared .NET framework or runtime. Protected even when no dependent app was detected."),
        (NewPattern(@"microsoft\s+visual\s+c\+\+.*redistributable"), "Shared Microsoft Visual C++ runtime. Never an automatic cleanup candidate."),
        (NewPattern(@"(?:microsoft\s+)?windows\s+app\s+(?:sdk\s+)?runtime"), "Windows App SDK shared runtime."),
        (NewPattern(@"(?:microsoft\s+)?edge\s+webview2\s+runtime"), "Shared WebView2 runtime used by desktop applications."),
        (NewPattern(@"(?:directx\s+(?:runtime|end.user)|vulkan\s+runtime)"), "Shared graphics runtime."),
        (NewPattern(@"(?:graphics|display|audio|chipset|bluetooth|wi.fi|wireless)\s+driver"), "Hardware driver or companion component. Manual OS/vendor tools only.")
    ];

    private static Regex NewPattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    public static (bool IsProtected, string Reason) Classify(string? name, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (true, "Unidentified software; cannot assess dependencies.");

        foreach (var (pattern, reason) in Rules)
        {
            try
            {
                if (pattern.IsMatch(name)) return (true, reason);
            }
            catch (RegexMatchTimeoutException)
            {
                return (true, "Could not evaluate component safely.");
            }
        }

        if (name.Contains("update", StringComparison.OrdinalIgnoreCase)
            && (publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false))
            return (true, "Microsoft updater or maintenance component; manual review only.");

        return (false, "No known shared-runtime signature matched. Other dependencies may still exist; manual review only.");
    }
}
