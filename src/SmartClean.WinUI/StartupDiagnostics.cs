using System.Runtime.InteropServices;
using System.Text;

namespace SmartClean.WinUI;

/// <summary>Local startup diagnostics only; never uploads data or changes scanned files.</summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();

    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClean", "logs", "startup.log");

    internal static void Record(string stage)
    {
        try
        {
            lock (Gate)
            {
                var path = LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:O}] PID={Environment.ProcessId} {stage}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never cause an additional startup failure.
        }
    }

    internal static void Report(string stage, Exception? exception)
    {
        var detail = exception?.ToString() ?? "No managed exception details available.";
        Record($"ERROR at {stage}:{Environment.NewLine}{detail}");
        try
        {
            MessageBoxW(IntPtr.Zero,
                $"SmartClean could not start ({stage}).\n\n" +
                $"Details were saved to:\n{LogFile}\n\n" +
                "If no log was created, run Run-SmartClean-Diagnostics.cmd.",
                "SmartClean startup error", 0x00000010);
        }
        catch (Exception) { /* A native loader error may prevent displaying a dialog. */ }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
