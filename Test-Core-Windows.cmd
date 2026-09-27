@echo off
setlocal EnableExtensions
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)
set "SDK_VERSION="
for /f "delims=" %%S in ('dotnet --version 2^>nul') do set "SDK_VERSION=%%S"
for /f "tokens=1 delims=." %%M in ("%SDK_VERSION%") do set "SDK_MAJOR=%%M"
if "%SDK_MAJOR%"=="10" (
    set "SDK_TARGET=net10.0"
) else if "%SDK_MAJOR%"=="9" (
    set "SDK_TARGET=net9.0"
) else (
    echo Active SDK is unsupported. Install .NET 10 or .NET 9.
    pause
    exit /b 1
)
dotnet restore "tests\SmartClean.Core.Tests\SmartClean.Core.Tests.csproj" --configfile "NuGet.Config" -p:SmartCleanTargetFramework=%SDK_TARGET%
if errorlevel 1 (
    echo Core test restore failed.
    pause
    exit /b 1
)
dotnet run --no-restore --project "tests\SmartClean.Core.Tests\SmartClean.Core.Tests.csproj" -c Release -p:SmartCleanTargetFramework=%SDK_TARGET%
if errorlevel 1 (
    echo Core tests failed. Do not distribute this build.
    pause
    exit /b 1
)
pause
