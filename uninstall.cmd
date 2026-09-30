@echo off
rem Double-click to uninstall.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch.ps1" -Uninstall %*
set rc=%errorlevel%
echo.
pause
exit /b %rc%
