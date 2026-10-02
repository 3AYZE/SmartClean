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
    private ServiceRelationshipRow[] _allServiceRows = [];
    private readonly List<CheckBox> _driveChecks = [];
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
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SupaClean.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
                StartupDiagnostics.Record("SupaClean window/taskbar icon applied");
            }
            else
            {
                StartupDiagnostics.Record("SupaClean icon asset not found; executable icon fallback will be used");
            }
        }
        catch (Exception ex)
        {
            // Icon failure must never prevent the cleaner from opening.
            StartupDiagnostics.Record("SupaClean window icon could not be applied: " + ex.Message);
        }
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
        PopulateDriveChoices();
        CurrentVersionText.Text = $"Installed version: {(typeof(MainWindow).Assembly.GetName().Version ?? ReleaseClient.InstalledVersion)}";
        RefreshRecoveryItems();
        // Do not hold up window activation or scanning for network access.
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void PopulateDriveChoices()
    {
        var previous = _driveChecks.Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadChoices = _driveChecks.Count > 0;
        _driveChecks.Clear();
        DriveChoicesPanel.Children.Clear();
        var roots = DriveScanCatalog.GetFixedReadyRoots();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        foreach (var root in roots)
        {
            var checkbox = new CheckBox
            {
                Content = root,
                Tag = root,
                IsChecked = hadChoices
                    ? previous.Contains(root)
                    : root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase),
                IsEnabled = AllDrivesCheckBox.IsChecked != true
            };
            checkbox.Checked += DriveChoice_Changed;
            checkbox.Unchecked += DriveChoice_Changed;
            _driveChecks.Add(checkbox);
            DriveChoicesPanel.Children.Add(checkbox);
        }
        ScanSelectedDrivesButton.IsEnabled = roots.Count > 0 && _scanCancellation is null;
        UpdateDriveSummary();
    }

    private void DriveChoice_Changed(object sender, RoutedEventArgs e) => UpdateDriveSummary();

    private void AllDrives_Changed(object sender, RoutedEventArgs e)
    {
        if (DriveChoicesPanel is null) return; // XAML may raise this during initialization.
        var all = AllDrivesCheckBox.IsChecked == true;
        foreach (var checkbox in _driveChecks)
            checkbox.IsEnabled = !all;
        UpdateDriveSummary();
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        PopulateDriveChoices();
    }

    private IReadOnlyList<string> GetSelectedDriveRoots() =>
        AllDrivesCheckBox.IsChecked == true
            ? _driveChecks.Select(c => (string)c.Tag).ToArray()
            : _driveChecks.Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag).ToArray();

    private void UpdateDriveSummary()
    {
        if (SelectedDriveSummary is null) return;
        var selected = GetSelectedDriveRoots();
        SelectedDriveSummary.Text = selected.Count == 0
            ? "No drives selected. Only ready fixed drives are available."
            : "Selected for read-only scan: " + string.Join(", ", selected)
              + ". No drive files can be removed from this page.";
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? destination = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        if (destination is null) return;
        OverviewView.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        AppsView.Visibility = destination == "apps" ? Visibility.Visible : Visibility.Collapsed;
        FoldersView.Visibility = destination == "folders" ? Visibility.Visible : Visibility.Collapsed;
        CleanupView.Visibility = destination == "cleanup" ? Visibility.Visible : Visibility.Collapsed;
        DependenciesView.Visibility = destination == "dependencies" ? Visibility.Visible : Visibility.Collapsed;
        SafetyView.Visibility = destination == "safety" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e) => await StartScanAsync();

    private async void ScanSelectedDrives_Click(object sender, RoutedEventArgs e) => await StartScanAsync(includeSelectedDrives: true);

    private async Task StartScanAsync(bool includeSelectedDrives = false)
    {
        if (_scanCancellation is not null) return;
        IReadOnlyList<string> selectedDriveRoots = includeSelectedDrives
            ? GetSelectedDriveRoots() : Array.Empty<string>();
        if (includeSelectedDrives && selectedDriveRoots.Count == 0)
        {
            SelectedDriveSummary.Text = "Select at least one available fixed drive or All fixed drives.";
            return;
        }
        using var cts = new CancellationTokenSource();
        _scanCancellation = cts;
        RefreshButton.IsEnabled = false;
        ScanSelectedDrivesButton.IsEnabled = false;
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
            var snapshot = await _scanner.ScanAsync(progress, cts.Token, selectedDriveRoots);
            if (cts.IsCancellationRequested) return;
            _snapshot = snapshot;
            ShowResults(snapshot);
            await RefreshTemporaryCandidatesAsync();
            ScanStatusText.Text = $"Last scanned {snapshot.FinishedAt:MMM d, yyyy · h:mm tt}";
            StatusInfo.Severity = snapshot.Warnings.Count > 0
                ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = snapshot.Warnings.Count > 0
                ? "Some folder entries were skipped" : "Scan complete";
            StatusInfo.Message = snapshot.Warnings.Count > 0
                ? $"{snapshot.Warnings.Count} folder notice(s): linked paths and unreadable files can be excluded. See Protection for the affected folders. Nothing was changed."
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
            ScanSelectedDrivesButton.IsEnabled = _driveChecks.Count > 0;
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
        var selectedDrives = result.Folders.Where(f => f.Label.StartsWith("Drive ",
            StringComparison.Ordinal)).ToArray();
        StorageSummary.Text = $"Downloads: {Formatting.Bytes(downloads?.Bytes)} · "
                            + $"Temporary folder: {Formatting.Bytes(temp?.Bytes)}. "
                            + (selectedDrives.Length == 0
                                ? "Choose fixed drives under Files & storage for a bounded drive-wide inventory."
                                : string.Join("; ", selectedDrives.Select(f => f.Label + ": "
                                    + Formatting.Bytes(f.Bytes)
                                    + (f.Truncated || f.Issues.Any(i => !i.IsExpected)
                                        ? " measured (partial lower bound)" : " measured (read-only)"))));
        var runtimes = RuntimeUsageAnalyzer.Build(result.Applications, result.Dependencies)
            .Select(r => new RuntimeUsageRow(r)).ToArray();
        RuntimeList.ItemsSource = runtimes;
        RuntimeSummary.Text = runtimes.Length == 0
            ? "No recognized .NET Core runtime entries found in Windows uninstall records. This does not prove that none are installed."
            : runtimes.Length.ToString("N0") + " registered .NET runtimes detected. Select one to see applications declaring that runtime family.";
        RuntimeDetails.Text = runtimes.Length == 0
            ? "Try a scan with installed .NET applications, but lack of evidence never justifies removal."
            : "Select a runtime to see the app declarations and evidence paths. Unknown or unobserved use remains protected.";

        var otherComponents = SharedComponentAnalyzer.Build(
            result.Applications, result.OtherComponentScan.Evidence)
            .Select(item => new SharedComponentRow(item)).ToArray();
        OtherComponentList.ItemsSource = otherComponents;
        OtherComponentSummary.Text = otherComponents.Length == 0
            ? "No registered or observed components matched the current supported families. This does not prove your apps have no shared dependencies."
            : otherComponents.Length.ToString("N0") + " installed or observed component entries · "
              + result.OtherComponentScan.BinariesRead.ToString("N0") + " native binaries inspected · "
              + result.OtherComponentScan.UnreadableFiles.ToString("N0") + " unreadable files · "
              + result.OtherComponentScan.UninspectedApps.ToString("N0") + " registered apps without accessible install evidence.";
        OtherComponentDetails.Text = "Select a component to see observed imports and package references. Matching an import to an installed runtime family does not prove which redistributable supplied it.";

        _allServiceRows = result.ServiceScan.Services
            .Select(service => new ServiceRelationshipRow(service)).ToArray();
        ServiceSummary.Text = result.ServiceScan.Services.Count.ToString("N0")
            + " service executable registrations inspected out of "
            + result.ServiceScan.Scanned.ToString("N0") + " entries · "
            + result.ServiceScan.Unreadable.ToString("N0") + " unavailable. "
            + "Associations are inferred from registered executable locations, not runtime-loading proof.";
        ApplyServiceFilter();
        var missingReferenceEntries = runtimes
            .Where(row => row.Usage.DeclaredDependents.Count == 0)
            .Select(row => new UnreferencedRow(row.Name, ".NET runtime",
                "No explicit matching declarations found; still protected."))
            .Concat(otherComponents
                .Where(row => row.Usage.Registered && row.Usage.Evidence.Count == 0)
                .Select(row => new UnreferencedRow(row.Name, row.Usage.Category,
                    "No matching native imports or package declarations found; still protected.")))
            .OrderBy(row => row.Type, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        UnreferencedList.ItemsSource = missingReferenceEntries;
        UnreferencedSummary.Text = missingReferenceEntries.Length.ToString("N0")
            + " installed shared component groups have no dependents observed in the limited scan. "
            + "Unknown, bundled, optional and rarely used dependencies remain possible.";

        var issueLines = result.Folders.SelectMany(folder =>
            folder.Issues.Select(issue =>
                (issue.IsExpected ? "Expected exclusion" : "Partial scan")
                + ": " + folder.Label + " — " + issue.Path + " — " + issue.Reason)).ToList();
        if (result.Warnings.Count > 0)
            issueLines.InsertRange(0, result.Warnings);
        WarningsList.ItemsSource = issueLines.Count > 0
            ? issueLines
            : ["No paths were skipped. Undeclared or dynamically loaded dependencies can still exist."];
        AppListSubtitle.Text = $"{result.Applications.Count:N0} registered entries · "
                             + "Registry size estimates may be missing or inaccurate.";
    }

    private void ServiceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A ComboBox may fire its initial event before the rest of the XAML tree exists.
        if (ServiceList is null || ServiceFilter is null) return;
        ApplyServiceFilter();
    }

    private void ApplyServiceFilter()
    {
        if (ServiceList is null || ServiceFilter is null) return;
        var filtered = ServiceFilter.SelectedIndex switch
        {
            1 => _allServiceRows.Where(row => row.Service.ExecutableUnderWindows),
            2 => _allServiceRows.Where(row => row.Service.AssociatedAppId is not null),
            3 => _allServiceRows.Where(row => row.Service.AssociatedAppId is null
                && !row.Service.ExecutableUnderWindows),
            _ => _allServiceRows.AsEnumerable()
        };
        ServiceList.ItemsSource = filtered.Take(250).ToArray();
        if (ServiceDetails is not null)
            ServiceDetails.Text = "Select a service to see its executable and registered-app association. "
                + "Only the first 250 matching registrations are displayed.";
    }

    private void ServiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServiceDetails is null) return;
        ServiceDetails.Text = ServiceList.SelectedItem is ServiceRelationshipRow row
            ? row.Details
            : "Select a service. Its registered executable path does not prove a specific runtime dependency.";
    }

    private void OtherComponentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OtherComponentDetails is null) return;
        OtherComponentDetails.Text = OtherComponentList.SelectedItem is SharedComponentRow row
            ? (row.InstallerParts.Length > 0 ? row.InstallerParts + "\n\n" : "") + row.Details
            : "Select a component. Missing observed references never prove a component is unused.";
    }

    private void RuntimeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuntimeDetails is null) return;
        RuntimeDetails.Text = RuntimeList.SelectedItem is RuntimeUsageRow row
            ? row.Details
            : "Select a runtime to view declarations. A blank list never proves a component is unused.";
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
            DetailRemovalStatus.Text = "Select an application to see available uninstall methods.";
            MsiUninstallButton.IsEnabled = false;
            OpenWindowsUninstallButton.IsEnabled = false;
            OpenInstallFolderButton.IsEnabled = false;
            return;
        }
        var app = row.App;
        DetailName.Text = app.Name;
        DetailPublisher.Text = $"{app.Publisher} · version {app.Version} · {row.Size}";
        DetailProtection.Text = (app.IsProtected ? "PROTECTED — " : "MANUAL REVIEW ONLY — ")
                               + app.ProtectionReason;
        var deps = _snapshot.Dependencies.Where(d => d.AppId == app.Id).ToArray();
        var native = _snapshot.OtherComponentScan.Evidence.Where(e => e.AppId == app.Id)
            .Take(16).ToArray();
        var detailLines = deps.Select(d => "• .NET " + d.FrameworkName
                + " (requested " + d.RequestedVersion + ")\n  Evidence: " + d.EvidencePath)
            .Concat(native.Select(e => "• " + e.Category + " — " + e.EvidenceType
                + "\n  " + e.Detail + "\n  Evidence: " + e.EvidencePath)).ToArray();
        DetailDependencies.Text = detailLines.Length == 0
            ? "No declarations or matching imports found in the bounded scan. Unknown or dynamically loaded dependencies may still exist."
            : string.Join("\n\n", detailLines);
        var ownedServices = _snapshot.ServiceScan.Services
            .Where(service => service.AssociatedAppId == app.Id)
            .Take(12).ToArray();
        if (ownedServices.Length > 0)
            DetailDependencies.Text += "\n\nRegistered services associated with this installation:\n"
                + string.Join("\n", ownedServices.Select(service =>
                    "• " + service.DisplayName + " (" + service.ServiceName + ")"
                    + "\n  Executable: " + service.ExecutablePath));
        DetailLocation.Text = app.InstallLocation ?? "Not reported in uninstall registry.";
        var removal = AppRemovalPlanner.Plan(app);
        DetailRemovalStatus.Text = removal.Summary;
        MsiUninstallButton.IsEnabled = removal.CanDirectUninstall;
        OpenWindowsUninstallButton.IsEnabled = !app.IsProtected;
        OpenInstallFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(app.InstallLocation)
            && Directory.Exists(app.InstallLocation);
    }


    // A standalone scan may be started from Cleanup without re-reading app inventory.
    private async void CleanupRefresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshTemporaryCandidatesAsync();

    private async Task RefreshTemporaryCandidatesAsync()
    {
        if (_cleanupBusy) return;
        CleanupRefreshButton.IsEnabled = false;
        CleanupSummary.Text = "Inspecting files in your temporary folder...";
        try
        {
            var scan = await Task.Run(() => _recovery.FindCandidates());
            TempCandidateList.ItemsSource = scan.Items.Select(x => new TempCandidateRow(x)).ToArray();
            ExcludedTempList.ItemsSource = scan.ExcludedFiles.Select(x => new TempExcludedRow(x)).ToArray();
            var bytes = scan.Items.Sum(x => x.Bytes);
            CleanupSummary.Text = scan.UnavailableReason is not null
                ? scan.UnavailableReason
                : $"{scan.Items.Count:N0} eligible files ({Formatting.Bytes(bytes)}) among "
                  + $"{scan.InspectedEntries:N0} inspected top-level files. "
                  + (scan.Items.Count == 0
                      ? "No files currently meet the strict age and type requirements. See exclusions below."
                      : "Select only the files you want to review for Recovery.")
                  + (scan.LimitReached ? " Scan limit reached; some files were not inspected." : "");
            ExcludedReasonSummary.Text = scan.UnavailableReason is not null
                ? "No excluded-file list is available until the temporary folder can be scanned."
                : scan.Skipped == 0
                    ? "No exclusions were reported."
                    : $"{scan.Skipped:N0} excluded or unreadable entries: "
                      + string.Join(" · ", scan.ExclusionReasons.OrderByDescending(x => x.Value)
                          .Select(x => $"{x.Value:N0} {x.Key}"))
                      + (scan.Skipped > scan.ExcludedFiles.Count
                          ? $". Displaying {scan.ExcludedFiles.Count:N0} examples."
                          : ".");
        }
        catch (Exception ex)
        {
            CleanupSummary.Text = "Temporary-file inspection failed: " + ex.Message;
            ExcludedReasonSummary.Text = "The exclusion list could not be updated.";
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
                ? "Recovery is empty. Moving files here does not free disk space."
                : $"{items.Count:N0} recoverable files · {Formatting.Bytes(items.Sum(x => x.Bytes))} still stored on disk. "
                  + "Select the ones you want to restore or review for permanent deletion.";
        }
        catch (Exception ex)
        {
            RecoverySummary.Text = "Recovery could not be read: " + ex.Message;
            StartupDiagnostics.Record("Recovery listing: " + ex);
        }
        RecoveryList_SelectionChanged(this, null!);
    }

    private void TempCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = TempCandidateList.SelectedItems.OfType<TempCandidateRow>().ToArray();
        MoveTempButton.IsEnabled = !_cleanupBusy && selected.Length > 0;
        CandidateSelectionSummary.Text = $"Selected: {selected.Length:N0} files · "
            + Formatting.Bytes(selected.Sum(x => x.Candidate.Bytes));
    }

    private void RecoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = RecoveryList.SelectedItems.OfType<RecoveryRow>().ToArray();
        RestoreButton.IsEnabled = !_cleanupBusy && selected.Length > 0;
        PurgeButton.IsEnabled = !_cleanupBusy && selected.Length > 0;
        RecoverySelectionSummary.Text = $"Selected: {selected.Length:N0} recovery files · "
            + Formatting.Bytes(selected.Sum(x => x.Item.Bytes));
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

    // A second review displays the COMPLETE selected file list, filenames,
    // original paths, and sizes. The user can uncheck any item or cancel.
    private async Task<T[]> ReviewExactFilesAsync<T>(string title, string explanation,
        IReadOnlyList<T> selected, Func<T, string> name, Func<T, string> path,
        Func<T, long> size, string approve)
    {
        if (selected.Count == 0) return [];
        var container = new StackPanel { Spacing = 10 };
        container.Children.Add(new TextBlock
        {
            Text = explanation,
            TextWrapping = TextWrapping.Wrap
        });
        var chosen = new List<(T Value, CheckBox Toggle)>(selected.Count);
        foreach (var entry in selected)
        {
            var toggle = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var lines = new StackPanel { Spacing = 2 };
            lines.Children.Add(new TextBlock
            {
                Text = name(entry) + " · " + Formatting.Bytes(size(entry)),
                TextWrapping = TextWrapping.Wrap
            });
            lines.Children.Add(new TextBlock
            {
                Text = path(entry),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75
            });
            toggle.Content = lines;
            container.Children.Add(toggle);
            chosen.Add((entry, toggle));
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = container,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = approve,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return [];
        return chosen.Where(x => x.Toggle.IsChecked is true).Select(x => x.Value).ToArray();
    }

    private void SetCleanupBusy(bool busy)
    {
        _cleanupBusy = busy;
        CleanupRefreshButton.IsEnabled = !busy;
        TempCandidateList.IsEnabled = !busy;
        RecoveryList.IsEnabled = !busy;
        TempCandidateList_SelectionChanged(this, null!);
        RecoveryList_SelectionChanged(this, null!);
    }

    private async void MoveTemp_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = TempCandidateList.SelectedItems.OfType<TempCandidateRow>()
            .Select(x => x.Candidate).ToArray();
        if (selected.Length == 0) return;
        var approved = await ReviewExactFilesAsync(
            "Review files to move into Recovery",
            "Uncheck anything you want to keep. SupaClean revalidates each selected file. "
            + "This move is reversible and does NOT free disk space.",
            selected,
            x => Path.GetFileName(x.FullPath),
            x => x.FullPath,
            x => x.Bytes,
            "Move checked files");
        if (approved.Length == 0) return;
        SetCleanupBusy(true);
        var succeeded = 0;
        var failures = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var candidate in approved)
                {
                    try { _recovery.MoveToRecovery(candidate); succeeded++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or InvalidOperationException or System.Security.SecurityException)
                    { failures.Add($"{Path.GetFileName(candidate.FullPath)}: {ex.Message}"); }
                }
            });
            StatusInfo.Severity = failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = "Recovery operation complete";
            StatusInfo.Message = $"{succeeded:N0} checked files moved; {failures.Count:N0} skipped. "
                + (failures.Count > 0 ? string.Join(" | ", failures.Take(2)) : "Nothing has been permanently deleted.");
            StatusInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "Recovery operation interrupted";
            StatusInfo.Message = ex.Message;
            StatusInfo.IsOpen = true;
            StartupDiagnostics.Record("Recovery move: " + ex);
        }
        finally
        {
            SetCleanupBusy(false);
            RefreshRecoveryItems();
            await RefreshTemporaryCandidatesAsync();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = RecoveryList.SelectedItems.OfType<RecoveryRow>().ToArray();
        if (selected.Length == 0) return;
        var approved = await ReviewExactFilesAsync(
            "Restore selected files",
            "Uncheck any files you do not want to restore. Existing files at the original path will never be overwritten.",
            selected, x => x.Name, x => x.OriginalPath, x => x.Item.Bytes,
            "Restore checked files");
        if (approved.Length == 0) return;
        SetCleanupBusy(true);
        var restored = 0;
        var errors = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var row in approved)
                {
                    try { _recovery.Restore(row.Item.Id); restored++; }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException
                        or UnauthorizedAccessException or System.Security.SecurityException)
                    { errors.Add($"{row.Name}: {ex.Message}"); }
                }
            });
            StatusInfo.Severity = errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = "Restore review complete";
            StatusInfo.Message = $"{restored:N0} files restored; {errors.Count:N0} could not be restored."
                + (errors.Count > 0 ? " " + string.Join(" | ", errors.Take(2)) : "");
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "Restore interrupted";
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
        if (_cleanupBusy) return;
        var selected = RecoveryList.SelectedItems.OfType<RecoveryRow>().ToArray();
        if (selected.Length == 0) return;
        var approved = await ReviewExactFilesAsync(
            "Final deletion list — permanent",
            "ONLY the checked Recovery payloads below will be permanently deleted. "
            + "Uncheck anything to keep or restore. This cannot be undone, and files will NOT go to the Recycle Bin.",
            selected, x => x.Name, x => x.OriginalPath, x => x.Item.Bytes,
            "Delete checked permanently");
        if (approved.Length == 0) return;
        SetCleanupBusy(true);
        var deleted = 0;
        long bytes = 0;
        var failures = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var row in approved)
                {
                    try { bytes += _recovery.PurgeFromRecovery(row.Item.Id); deleted++; }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException
                        or UnauthorizedAccessException or System.Security.SecurityException)
                    { failures.Add($"{row.Name}: {ex.Message}"); }
                }
            });
            StatusInfo.Severity = failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            StatusInfo.Title = "Permanent deletion review complete";
            StatusInfo.Message = $"{deleted:N0} checked Recovery payloads deleted "
                + $"({Formatting.Bytes(bytes)}); {failures.Count:N0} not deleted."
                + (failures.Count > 0 ? " " + string.Join(" | ", failures.Take(2)) : "");
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "Permanent deletion interrupted";
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

    private async void MsiUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row) return;
        var app = row.App;
        var removal = AppRemovalPlanner.Plan(app);
        if (app.IsProtected || !removal.CanDirectUninstall
            || removal.Method != AppRemovalMethod.WindowsInstaller
            || string.IsNullOrWhiteSpace(removal.ProductCode))
            return;

        var evidenceCount = (_snapshot?.Dependencies.Count(d => d.AppId == app.Id) ?? 0)
            + (_snapshot?.OtherComponentScan.Evidence.Count(d => d.AppId == app.Id) ?? 0);
        var serviceCount = _snapshot?.ServiceScan.Services.Count(d => d.AssociatedAppId == app.Id) ?? 0;
        var size = Formatting.Bytes(app.EstimatedSizeBytes);

        if (!await AskConfirmationAsync("Uninstall this application",
            $"{app.Name}\n{app.Publisher} · {app.Version} · {size}\n\n"
            + $"Observed dependency references: {evidenceCount:N0}\n"
            + $"Associated registered services: {serviceCount:N0}\n\n"
            + "SupaClean will launch Windows Installer with a validated product GUID. "
            + "Windows will show the uninstall UI and may request administrator approval. "
            + "No application folders or registry uninstall commands are deleted or executed directly.",
            "Open uninstaller")) return;

        try
        {
            var msiexec = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            if (!File.Exists(msiexec)) msiexec = "msiexec.exe";
            Process.Start(new ProcessStartInfo
            {
                FileName = msiexec,
                Arguments = "/x " + removal.ProductCode,
                UseShellExecute = true
            });
            StatusInfo.Severity = InfoBarSeverity.Informational;
            StatusInfo.Title = "Windows Installer opened";
            StatusInfo.Message = $"Review the Windows uninstall dialog for {app.Name}. "
                + "After it finishes, run a new SupaClean scan to refresh the application list.";
            StatusInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Warning;
            StatusInfo.Title = "Could not open Windows Installer";
            StatusInfo.Message = ex.Message;
            StatusInfo.IsOpen = true;
            StartupDiagnostics.Record("Launching validated MSI uninstall: " + ex);
        }
    }

    private async void OpenWindowsUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row || row.App.IsProtected) return;
        if (!await AskConfirmationAsync("Open Windows Installed apps",
            $"{row.Name}\n\n"
            + "SupaClean could not validate a direct MSI removal method or you chose Windows review. "
            + "Windows will perform the actual uninstall if you select Uninstall there. "
            + "SupaClean does not execute arbitrary uninstall commands stored in the registry.",
            "Open Windows Settings")) return;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            StatusInfo.Severity = InfoBarSeverity.Informational;
            StatusInfo.Title = "Windows Installed apps opened";
            StatusInfo.Message = $"Find {row.Name}, review it, and choose Uninstall there if appropriate.";
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

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row
            || string.IsNullOrWhiteSpace(row.App.InstallLocation)
            || !Directory.Exists(row.App.InstallLocation)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = row.App.InstallLocation,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusInfo.Severity = InfoBarSeverity.Warning;
            StatusInfo.Title = "Could not open install folder";
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
            UpdateStatusText.Text = "Installer opened. Close SupaClean when requested to finish updating.";
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

