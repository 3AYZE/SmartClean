using Microsoft.UI.Xaml;
using SmartClean.Core.Updates;

namespace SmartClean.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        StartupDiagnostics.Record("App constructor entered");
        try
        {
            InitializeComponent();
            UnhandledException += (_, eventArgs) =>
            {
                StartupDiagnostics.Report("Unhandled WinUI exception", eventArgs.Exception);
                // Do not mark as handled: continuing after an unknown XAML failure is unsafe.
            };
            StartupDiagnostics.Record("App XAML initialized");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Report("App XAML initialization", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Record("OnLaunched entered");
        try
        {
            _window = new MainWindow();
            StartupDiagnostics.Record("MainWindow constructed");
            _window.Activate();
            StartupDiagnostics.Record("MainWindow activated");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Report("Creating or showing the main window", ex);
            throw;
        }
    }
}
