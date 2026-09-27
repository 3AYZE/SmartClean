param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDir = Join-Path $project 'output\logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$report = Join-Path $logDir 'launch-diagnostics.txt'

try {
    Start-Transcript -LiteralPath $report -Force | Out-Null
    Write-Host '=== SMARTCLEAN STARTUP DIAGNOSTICS (v0.1.4) ==='
    Write-Host 'This tool only starts SmartClean and reads Windows startup events.'
    Write-Host 'It does not delete, clean, or modify applications or user files.'
    Write-Host ''
    Write-Host ('PowerShell: ' + $PSVersionTable.PSVersion)
    Write-Host ('64-bit PowerShell: ' + [Environment]::Is64BitProcess)
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        Write-Host ('Windows: ' + $os.Caption + ' build ' + $os.BuildNumber)
    } catch { Write-Host ('Windows version unavailable: ' + $_.Exception.Message) }
    Write-Host ('Requested architecture: ' + $Architecture)

    $publish = Join-Path $project ("output\SmartClean-win-$Architecture")
    $exe = Join-Path $publish 'SmartClean.WinUI.exe'
    foreach ($file in @('SmartClean.WinUI.exe', 'SmartClean.WinUI.dll', 'SmartClean.WinUI.runtimeconfig.json', 'Microsoft.ui.xaml.dll')) {
        $found = Test-Path -LiteralPath (Join-Path $publish $file)
        Write-Host (('{0}: {1}' -f $file, $(if ($found) { 'PRESENT' } else { 'NOT FOUND (may be bundled)' })))
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Write-Host ''
        Write-Host 'ERROR: No published EXE found. Run Build-Windows.cmd and check output\logs\publish.log.'
    } else {
        Write-Host ''
        Write-Host 'Starting SmartClean; waiting 10 seconds for startup...'
        $started = Get-Date
        try {
            $process = Start-Process -FilePath $exe -WorkingDirectory $publish -PassThru -ErrorAction Stop
            Start-Sleep -Seconds 10
            $process.Refresh()
            if ($process.HasExited) {
                $exitCode = [uint32]($process.ExitCode -band 0xffffffffL)
                Write-Host ('Process EXITED. Exit code: {0} (0x{1:X8})' -f $process.ExitCode, $exitCode)
            } else {
                Write-Host ('Process STILL RUNNING, PID: ' + $process.Id)
                Write-Host ('Main window handle: ' + $process.MainWindowHandle)
                Write-Host 'If the app is visible, close it normally and you can ignore startup diagnostics.'
                Write-Host 'If it is not visible, leave it running and include this report.'
            }
        } catch {
            Write-Host ('Could not launch: ' + $_.ToString())
        }

        # Read this FIRST. Event Log queries can be slow on some Windows systems;
        # the previous runner sometimes produced a truncated report before it finished.
        Write-Host ''
        Write-Host '=== APP STARTUP LOG (most recent 70 lines) ==='
        $startupLog = Join-Path $env:LOCALAPPDATA 'SmartClean\logs\startup.log'
        if (Test-Path -LiteralPath $startupLog -PathType Leaf) {
            try {
                Get-Item -LiteralPath $startupLog | ForEach-Object {
                    Write-Host ('Startup log last modified: ' + $_.LastWriteTime.ToString('o'))
                }
                Get-Content -LiteralPath $startupLog -Tail 70 | ForEach-Object { Write-Host $_ }
            } catch { Write-Host ('Could not read startup log: ' + $_.Exception.Message) }
        } else {
            Write-Host 'No managed startup log. Failure may precede the application entry point.'
        }
        Write-Host ''
        Write-Host '=== RECENT MATCHING WINDOWS APPLICATION EVENTS ==='
        Write-Host 'Query limited to recent app crash events.'
        try {
            $events = @(Get-WinEvent -FilterHashtable @{
                LogName='Application'; Id=1000,1001,1026; StartTime=$started.AddSeconds(-3)
            } -MaxEvents 25 -ErrorAction Stop |
                Where-Object { $_.Message -match 'SmartClean(\.WinUI)?(\.exe)?' } |
                Select-Object -First 5)
            if ($events.Count -gt 0) {
                foreach ($evt in $events) {
                    Write-Host ('Event ' + $evt.Id + ', ' + $evt.ProviderName + ', ' + $evt.TimeCreated)
                    Write-Host $evt.Message
                    Write-Host '---'
                }
            } else {
                Write-Host 'No matching crash event found. The native stowed exception details may require an Event Viewer report or debugger dump.'
            }
        } catch {
            Write-Host ('Windows event lookup returned: ' + $_.Exception.Message)
        }

    }
    Write-Host ''
    Write-Host 'If sharing this report, you may redact your Windows username from local paths.'
} catch {
    Write-Host ('Unexpected diagnostics error: ' + $_.ToString())
} finally {
    try { Stop-Transcript | Out-Null } catch { }
    Write-Host ('Saved report: ' + $report)
}
