@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

rem This file changes no global SDK, NuGet, firewall, or proxy settings.
if not exist "output\logs" mkdir "output\logs"
if not exist "NuGet.Config" (
    echo ERROR: NuGet.Config is missing. Extract the complete ZIP before building.
    pause
    exit /b 1
)
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK not found. Install the Windows .NET 10 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

set "TARGET=win-x64"
set "PLATFORM=x64"
if /I "%~1"=="arm64" (
    set "TARGET=win-arm64"
    set "PLATFORM=arm64"
)
if /I not "%~1"=="arm64" if /I not "%~1"=="x64" if not "%~1"=="" (
    echo ERROR: Unknown architecture. Use x64 or arm64.
    pause
    exit /b 1
)

rem Use the ACTIVE SDK (respecting global.json); the earlier build checked installed
rem SDKs instead and could select a target the active SDK could not compile.
set "SDK_VERSION="
for /f "delims=" %%S in ('dotnet --version 2^>nul') do set "SDK_VERSION=%%S"
if not defined SDK_VERSION (
    echo ERROR: No active .NET SDK. Check global.json and dotnet --list-sdks.
    pause
    exit /b 1
)
for /f "tokens=1 delims=." %%M in ("%SDK_VERSION%") do set "SDK_MAJOR=%%M"
if "%SDK_MAJOR%"=="10" (
    set "SDK_TARGET=net10.0"
) else if "%SDK_MAJOR%"=="9" (
    set "SDK_TARGET=net9.0"
) else (
    echo ERROR: Active .NET SDK %SDK_VERSION% is unsupported.
    echo Install .NET 10 SDK or adjust your global.json.
    pause
    exit /b 1
)

echo.
echo ===============================================
echo SupaClean 0.1.4 - read-only Windows build
echo Target: %TARGET%  Framework: %SDK_TARGET%  SDK: %SDK_VERSION%
echo ===============================================
echo.
echo Package source for THIS PROJECT:
dotnet nuget list source --configfile "NuGet.Config"
echo.
echo Checking whether the official NuGet feed responds...
powershell.exe -NoProfile -Command "try { $r=Invoke-WebRequest -Uri 'https://api.nuget.org/v3/index.json' -UseBasicParsing -TimeoutSec 12 -ErrorAction Stop; Write-Host 'NuGet.org reachable.'; exit 0 } catch { Write-Host ('Warning: NuGet.org connectivity check failed: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 (
    echo If restore fails, check Internet access, proxy or firewall settings.
    echo You can open https://api.nuget.org/v3/index.json in your browser to compare.
    echo The build will still try to restore, in case packages are cached.
)
echo.
echo Restoring only the selected architecture: %TARGET%...
echo Restore log: output\logs\restore.log
rem Keep restore separate so a failed first build produces one clear diagnostic log.
dotnet restore "src\SmartClean.WinUI\SmartClean.WinUI.csproj" --configfile "NuGet.Config" -r "%TARGET%" -p:SmartCleanTargetFramework=%SDK_TARGET% -p:Platform=%PLATFORM% -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:EnableMsixTooling=true --verbosity minimal > "output\logs\restore.log" 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: Package restore failed. First diagnostic messages:
    powershell.exe -NoProfile -Command "Get-Content -LiteralPath 'output\logs\restore.log' -TotalCount 35"
    echo.
    echo If you see NU1100 for many Microsoft packages, NuGet.org may be blocked.
    echo Test: https://api.nuget.org/v3/index.json
    echo If you see NU1301, check your Internet, proxy and DNS settings.
    echo Full log: output\logs\restore.log
    echo Run Diagnose-Windows.cmd and share its output if this persists.
    pause
    exit /b 1
)
echo.
echo Restored. Publishing read-only preview...
rem WinUI 3 unpackaged self-contained publish requires its native MSIX build assets.
rem Trim/AOT/single-file are disabled to preserve WinRT and XAML metadata.
dotnet publish "src\SmartClean.WinUI\SmartClean.WinUI.csproj" --no-restore -c Release -r "%TARGET%" --self-contained true -p:SmartCleanTargetFramework=%SDK_TARGET% -p:Platform=%PLATFORM% -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true -p:PublishTrimmed=false -p:PublishAot=false -p:PublishReadyToRun=false -p:PublishSingleFile=false -o "output\SmartClean-%TARGET%" > "output\logs\publish.log" 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: Compilation or publishing failed. First diagnostic messages:
    powershell.exe -NoProfile -Command "Get-Content -LiteralPath 'output\logs\publish.log' -TotalCount 35"
    echo.
    echo Full log: output\logs\publish.log
    echo Run Diagnose-Windows.cmd if the error is related to the SDK.
    pause
    exit /b 1
)
echo.
echo SUCCESS. Open output\SmartClean-%TARGET%\SmartClean.WinUI.exe
echo Keep the whole published folder together. This preview cannot delete files.
echo If double-click shows no window, run Run-SmartClean-Diagnostics.cmd.
pause
exit /b 0
