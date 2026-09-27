@echo off
setlocal EnableExtensions
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-SmartClean-Diagnostics.ps1" %*
echo.
echo Diagnostic report: output\logs\launch-diagnostics.txt
echo If SmartClean does not open, send that report and output\logs\publish.log.
pause
