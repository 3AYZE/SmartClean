using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartClean.Core;
using SmartClean.Core.Cleanup;
using SmartClean.Core.Updates;
using System.Diagnostics;
using SmartClean.WinUI.Models;

namespace SmartClean.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ScanCoordinator _scanner = new();
    private readonly TempRecoveryManager _recovery = new();
    private bool _cleanupBusy;
    private CancellationTokenSource? _scanCancellation;
    private ScanSnapshot? _snapshot;
    private AppRow[] _allApps = [];
    private readonly ReleaseClient _releases = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private ReleaseInfo? _availableUpdate;
    private bool _checkingUpdates;
    private bool _installingUpdate;

    public MainWindow()
    {
        StartupDiagnostics.Record("MainWindow constructor entered (before XAML)");
        InitializeComponent();
        StartupDiagnostics.Record("MainWindow XAML initialized");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        StartupDiagnostics.Record("MainWindow title bar configured");
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 800)); }
        catch (Exception) { /* Respect window manager constraints. */ }
        StartupDiagnostics.Record("MainWindow initializing navigation");
        Nav.SelectedItem = Nav.MenuItems[0];
        StartupDiagnostics.Record("MainWindow navigation ready");
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _updateCancellation.Cancel();
            _releases.Dispose();
        };
        CurrentVersionText.Text = $"Installed version: {(typeof(MainWindow).Assembly.GetName().Version ?? ReleaseClient.InstalledVersion)}";
        RefreshRecoveryItems();
        // Do not hold up window activation or scanning for network access.
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? destination = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        if (destination is null) return;
        OverviewView.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        AppsView.Visibility = destination == "apps" ? Visibility.Visible : Visibility.Collapsed;
        FoldersView.Visibility = destination == "folders" ? Visibility.Visible : Visibility.Collapsed;
        CleanupView.Visibility = destination == "cleanup" ? Visibility.Visible : Visibility.Collapsed;
        SafetyView.Visibility = destination == "safety" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e) => await StartScanAsync();

    private async Task StartScanAsync()
    {
        if (_scanCancellation is not null) return;
        using var cts = new CancellationTokenSource();
        _scanCancellation = cts;
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ScanRing.IsActive = true;
        ScanStatusText.Text = "Preparing scan...";
        StatusInfo.Severity = InfoBarSeverity.Informational;
        StatusInfo.Title = "Scanning";
        StatusInfo.IsOpen = true;
        StatusInfo.Message = "Reading only. No files, application registrations or settings are changed.";
        var progress = new Progress<string>(message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_scanCancellation == cts) ScanStatusText.Text = message;
            });
        });
        try
        {
            var snapshot = await _scanner.ScanAsync(progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            _snapshot = snapshot;
            ShowResults(snapshot);
            await RefreshTemporaryCandidatesAsync();
            ScanStatusText.Text = $"Last scanned {snapshot.FinishedAt:MMM d, yyyy · h:mm tt}";
            StatusInfo.Severity = snapshot.Warnings.Count > 0
                ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = snapshot.Warnings.Count > 0
                ? "Some folder totals are partial" : "Scan complete";
            StatusInfo.Message = snapshot.Warnings.Count > 0
                ? $"{snapshot.Warnings.Count} storage scan limitation(s). See Protection for the affected folders. Nothing was changed."
                : "Inventory and temporary-file candidates are ready. No files were changed.";
            StatusInfo.IsOpen = snapshot.Warnings.Count > 0;
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Scan canceled. Previous results were kept.";
            StatusInfo.IsOpen = true;
            StatusInfo.Title = "Canceled";
            StatusInfo.Message = "The scan stopped. No files or applications were changed.";
        }
        catch (Exception e)
        {
            ScanStatusText.Text = "Scan could not finish.";
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.IsOpen = true;
            StatusInfo.Title = "Scan failed";
            StatusInfo.Message = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            if (_scanCancellation == cts) _scanCancellation = null;
            ScanRing.IsActive = false;
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _scanCancellation?.Cancel();
    }

    private void ShowResults(ScanSnapshot result)
    {
        AppsCount.Text = result.Applications.Count.ToString("N0");
        ProtectedCount.Text = result.ProtectedAppCount.ToString("N0");
        DependencyCount.Text = result.ObservedDependencyCount.ToString("N0");
        var mainDrive = result.Disks.FirstOrDefault(d => d.Name.Equals(
                Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            ?? result.Disks.FirstOrDefault();
        if (mainDrive is not null && mainDrive.CapacityBytes > 0)
        {
            var used = mainDrive.CapacityBytes - mainDrive.FreeBytes;
            DiskSizeText.Text = $"{Formatting.Bytes(used)} of {Formatting.Bytes(mainDrive.CapacityBytes)} used";
            DiskFreeText.Text = $"{Formatting.Bytes(mainDrive.FreeBytes)} available on {mainDrive.Name}";
            DiskBar.Value = Math.Clamp(100.0 * used / mainDrive.CapacityBytes, 0, 100);
        }
        else
        {
            DiskSizeText.Text = "Drive information not available";
            DiskFreeText.Text = "No fixed drive was readable.";
            DiskBar.Value = 0;
        }

        _allApps = result.Applications.Select(a => new AppRow(a)).ToArray();
        ApplyAppFilter();
        FolderList.ItemsSource = result.Folders.Select(f => new FolderRow(f)).ToArray();
        var temp = result.Folders.FirstOrDefault(f => !f.IsPersonalData);
        var downloads = result.Folders.FirstOrDefault(f => f.IsPersonalData);
        StorageSummary.Text = $"Downloads: {Formatting.Bytes(downloads?.Bytes)} · "
                            + $"Temporary folder: {Formatting.Bytes(temp?.Bytes)}. "
                            + (result.Folders.Any(f => f.Truncated || f.SkippedEntries > 0)
                                ? "Some directories were inaccessible or reached a scan limit."
                                : "Both folders were measured without modifying anything.");
        WarningsList.ItemsSource = result.Warnings.Count > 0
            ? result.Warnings
            : ["No folder measurement errors were reported. Dependency discovery is still limited to explicit .NET runtime declarations."];
        AppListSubtitle.Text = $"{result.Applications.Count:N0} registered entries · "
                             + "Registry size estimates may be missing or inaccurate.";
    }

    private void AppsSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyAppFilter();

    private void ApplyAppFilter()
    {
        if (AppList is null || AppsSearch is null) return;
        var text = AppsSearch.Text.Trim();
        AppList.ItemsSource = string.IsNullOrEmpty(text)
            ? _allApps
            : _allApps.Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                               || a.App.Publisher.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row || _snapshot is null)
        {
            DetailName.Text = "Choose an application";
            DetailPublisher.Text = "View its available installation and dependency evidence.";
            DetailProtection.Text = "No item selected.";
            DetailDependencies.Text = "Select an item to inspect its evidence.";
            DetailLocation.Text = "—";
            OpenWindowsUninstallButton.IsEnabled = false;
            return;
        }
        var app = row.App;
        DetailName.Text = app.Name;
        DetailPublisher.Text = $"{app.Publisher} · version {app.Version} · {row.Size}";
        DetailProtection.Text = (app.IsProtected ? "PROTECTED — " : "MANUAL REVIEW ONLY — ")
                               + app.ProtectionReason;
        var deps = _snapshot.Dependencies.Where(d => d.AppId == app.Id).ToArray();
        DetailDependencies.Text = deps.Length == 0
            ? "No explicit .NET runtime declarations found in the bounded scan. Other dependencies may exist."
            : string.Join("\n", deps.Select(d =>
                $"• {d.FrameworkName} (requested {d.RequestedVersion})\n   Evidence: {d.EvidencePath}"));
        DetailLocation.Text = app.InstallLocation ?? "Not reported in uninstall registry.";
        OpenWindowsUninstallButton.IsEnabled = !app.IsProtected;
    }


    // A standalone scan may be started from Cleanup without re-reading app inventory.
    private async void CleanupRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshTemporaryCandidatesAsync();

    private async Task RefreshTemporaryCandidatesAsync()
    {
        if (_cleanupBusy) return;
        CleanupRefreshButton.IsEnabled = false;
        CleanupSummary.Text = "Checking eligible top-level temporary files...";
        try
        {
            var scan = await Task.Run(() => _recovery.FindCandidates());
            TempCandidateList.ItemsSource = scan.Items
                .Select(x => new TempCandidateRow(x)).ToArray();
            var bytes = scan.Items.Sum(x => x.Bytes);
            CleanupSummary.Text = scan.UnavailableReason is not null
                ? scan.UnavailableReason
                : $"{scan.Items.Count:N0} candidates · {Formatting.Bytes(bytes)} in Recovery if moved. "
                  + "This is not free space until you permanently delete reviewed recovery items."
                  + (scan.LimitReached ? " Candidate limit reached; results are incomplete." : "")
                  + (scan.Skipped > 0 ? $" {scan.Skipped:N0} other entries were excluded or unreadable." : "");
        }
        catch (Exception ex)
        {
            CleanupSummary.Text = "Temporary-file scan was unsuccessful: " + ex.Message;
            StartupDiagnostics.Record("Cleanup discovery: " + ex);
        }
        finally
        {
            CleanupRefreshButton.IsEnabled = !_cleanupBusy;
            TempCandidateList_SelectionChanged(this, null!);
        }
    }

    private void RefreshRecoveryItems()
    {
        try
        {
            var items = _recovery.ListRecovery();
            RecoveryList.ItemsSource = items.Select(x => new RecoveryRow(x)).ToArray();
            RecoverySummary.Text = items.Count == 0
                ? "No files are currently held in Recovery."
                : $"{items.Count:N0} recoverable file(s) · {Formatting.Bytes(items.Sum(x => x.Bytes))} still stored on disk.";
        }
        catch (Exception ex)
        {
            RecoverySummary.Text = "Recovery could not be read: " + ex.Message;
            StartupDiagnostics.Record("Recovery listing: " + ex);
        }
        RecoveryList_SelectionChanged(this, null!);
    }

    private void TempCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MoveTempButton.IsEnabled = !_cleanupBusy
            && TempCandidateList.SelectedItems.Count > 0;

    private void RecoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var valid = !_cleanupBusy && RecoveryList.SelectedItem is RecoveryRow;
        RestoreButton.IsEnabled = valid;
        PurgeButton.IsEnabled = valid;
    }

    private async Task<bool> AskConfirmationAsync(string title, string message, string approve)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = approve,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetCleanupBusy(bool busy)
    {
        _cleanupBusy = busy;
        CleanupRefreshButton.IsEnabled = !busy;
        TempCandidateList_SelectionChanged(this, null!);
        RecoveryList_SelectionChanged(this, null!);
    }

    private async void MoveTemp_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = TempCandidateList.SelectedItems.OfType<TempCandidateRow>()
            .Select(x => x.Candidate).ToArray();
        if (selected.Length == 0) return;
        var total = selected.Sum(x => x.Bytes);
        if (!await AskConfirmationAsync("Move temporary files to Recovery",
            $"Move {selected.Length:N0} selected file(s) ({Formatting.Bytes(total)})? "
            + "SmartClean will recheck each one and skip anything changed or in use. "
            + "This action does not reclaim storage until you separately delete them from Recovery.",
            "Move to Recovery")) return;
        SetCleanupBusy(true);
        var succeeded = 0;
        var failures = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var candidate in selected)
                {
                    try { _recovery.MoveToRecovery(candidate); succeeded++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or InvalidOperationException or System.Security.SecurityException)
                    { failures.Add($"{Path.GetFileName(candidate.FullPath)}: {ex.Message}"); }
                }
            });
            RefreshRecoveryItems();
            StatusInfo.Severity = failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = "Recovery operation complete";
            StatusInfo.Message = $"{succeeded:N0} file(s) moved to Recovery; {failures.Count:N0} skipped. "
                + (failures.Count > 0 ? string.Join(" | ", failures.Take(2)) : "No files were deleted.");
            StatusInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "Recovery operation interrupted";
            StatusInfo.Message = ex.Message;
            StatusInfo.IsOpen = true;
        }
        finally
        {
            SetCleanupBusy(false);
            await RefreshTemporaryCandidatesAsync();
            RefreshRecoveryItems();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy || RecoveryList.SelectedItem is not RecoveryRow row) return;
        SetCleanupBusy(true);
        try
        {
            await Task.Run(() => _recovery.Restore(row.Item.Id));
            StatusInfo.Severity = InfoBarSeverity.Success;
            StatusInfo.Title = "File restored";
            StatusInfo.Message = "The file was moved back to its original temporary folder without overwriting anything.";
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Warning;
            StatusInfo.Title = "Restore did not complete";
            StatusInfo.Message = ex.Message;
        }
        finally
        {
            StatusInfo.IsOpen = true;
            SetCleanupBusy(false);
            RefreshRecoveryItems();
            await RefreshTemporaryCandidatesAsync();
        }
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy || RecoveryList.SelectedItem is not RecoveryRow row) return;
        if (!await AskConfirmationAsync("Permanently delete this recovery file",
            $"{row.Name}\nOriginal: {row.OriginalPath}\n\n"
            + "This permanently deletes only this selected Recovery payload. "
            + "It does NOT move it to the Recycle Bin and cannot be undone.",
            "Delete permanently")) return;

        SetCleanupBusy(true);
        try
        {
            var bytes = await Task.Run(() => _recovery.PurgeFromRecovery(row.Item.Id));
            StatusInfo.Severity = InfoBarSeverity.Success;
            StatusInfo.Title = "Recovery item deleted";
            StatusInfo.Message = $"Permanently deleted the selected payload ({Formatting.Bytes(bytes)}). "
                + "Actual free space depends on the filesystem.";
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Warning;
            StatusInfo.Title = "Deletion was not completed";
            StatusInfo.Message = ex.Message;
        }
        finally
        {
            StatusInfo.IsOpen = true;
            SetCleanupBusy(false);
            RefreshRecoveryItems();
            await RefreshTemporaryCandidatesAsync();
        }
    }

    private async void OpenWindowsUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row || row.App.IsProtected) return;
        if (!await AskConfirmationAsync("Review uninstall in Windows",
            $"{row.Name}\n\nSmartClean has NOT verified all dependency relationships. "
            + "Windows will perform the actual uninstall, if you choose it there. "
            + "SmartClean will never execute a command stored in the uninstall registry.",
            "Open Windows Settings")) return;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            StatusInfo.Severity = InfoBarSeverity.Informational;
            StatusInfo.Title = "Windows Settings opened";
            StatusInfo.Message = "Locate the application by name, review its effects, and uninstall there if appropriate.";
            StatusInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Warning;
            StatusInfo.Title = "Could not open Windows Settings";
            StatusInfo.Message = ex.Message;
            StatusInfo.IsOpen = true;
        }
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootGrid is null || ThemePicker is null) return;
        RootGrid.RequestedTheme = ThemePicker.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private void ReviewUpdate_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems.Cast<NavigationViewItem>().First(i => (string)i.Tag == "settings");
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _installingUpdate || _updateCancellation.IsCancellationRequested) return;
        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        if (manual) UpdateStatusText.Text = "Checking GitHub for the newest compiled Windows release...";
        try
        {
            var release = await _releases.GetLatestAsync(_updateCancellation.Token);
            if (_updateCancellation.IsCancellationRequested) return;
            if (release.Version > (typeof(MainWindow).Assembly.GetName().Version ?? ReleaseClient.InstalledVersion))
            {
                _availableUpdate = release;
                UpdateInfo.Message = $"Version {release.Version} is available. Review it before installing.";
                UpdateInfo.IsOpen = true;
                UpdateStatusText.Text = $"Version {release.Version} is available. Published on GitHub.";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                _availableUpdate = null;
                UpdateInfo.IsOpen = false;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"This installation matches the latest published Windows build ({release.Version}). A newer GitHub source commit becomes installable after its build and tests finish.";
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StartupDiagnostics.Record($"Update check: {ex.GetType().Name}: {ex.Message}");
            if (!_updateCancellation.IsCancellationRequested)
                UpdateStatusText.Text = $"Update check unavailable: {ex.Message}";
        }
        finally
        {
            _checkingUpdates = false;
            if (!_updateCancellation.IsCancellationRequested) CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } release || _installingUpdate) return;
        _installingUpdate = true;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateDownloadBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Downloading verified installer for {release.Version}...";
        var progress = new Progress<double>(fraction => UpdateDownloadBar.Value = fraction * 100);
        try
        {
            string installer = await _releases.DownloadAndVerifyAsync(release, progress, _updateCancellation.Token);
            if (_updateCancellation.IsCancellationRequested) return;
            // Consent happened when clicking Download and install. Never run arbitrary URLs.
            // Inno Setup installs per-user and prompts to close in-use programs if needed.
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            UpdateStatusText.Text = "Installer opened. Close SmartClean when requested to finish updating.";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Update download canceled.";
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Record($"Update install: {ex.GetType().Name}: {ex.Message}");
            UpdateStatusText.Text = $"Update was not installed: {ex.Message}";
        }
        finally
        {
            _installingUpdate = false;
            if (!_updateCancellation.IsCancellationRequested)
            {
                CheckUpdatesButton.IsEnabled = true;
                InstallUpdateButton.IsEnabled = true;
            }
            UpdateDownloadBar.Visibility = Visibility.Collapsed;
        }
    }
}

