@echo off
REM -----------------------------------------------------------------------------
REM NetDaemonApps Quick Publish (Windows Batch)
REM Double-click this file to publish your NetDaemon apps
REM -----------------------------------------------------------------------------

echo.
echo =====================================
echo   NetDaemonApps Quick Publish
echo =====================================
echo.

REM Change to script directory
cd /d "%~dp0"

REM Run the PowerShell publish script
powershell.exe -ExecutionPolicy Bypass -File "publish.ps1"

REM Keep window open to see results
echo.
echo Press any key to close this window...
pause >nul
