@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo === SMARTCLEAN BUILD DIAGNOSTICS ===
echo Current folder: %CD%
echo.
where dotnet
echo.
echo Active SDK:
dotnet --version
echo.
echo Installed SDKs:
dotnet --list-sdks
echo.
echo NuGet sources for THIS PROJECT:
dotnet nuget list source --configfile NuGet.Config
echo.
echo Testing HTTPS access to official NuGet feed:
powershell.exe -NoProfile -Command "try { $r=Invoke-WebRequest -Uri 'https://api.nuget.org/v3/index.json' -UseBasicParsing -TimeoutSec 12 -ErrorAction Stop; Write-Host ('SUCCESS: HTTP ' + [int]$r.StatusCode); exit 0 } catch { Write-Host ('FAILED: ' + $_.Exception.Message); exit 1 }"
echo.
echo If the check fails, test the same URL in a browser. Do not disable TLS verification.
echo If your network uses a corporate proxy, ask your network administrator for settings.
echo.
echo If restore fails, include output\logs\restore.log and the FIRST error only.
pause
