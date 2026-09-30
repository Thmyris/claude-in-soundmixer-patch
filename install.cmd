@echo off
rem Double-click to install. Arguments are passed on to patch.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch.ps1" %*
set rc=%errorlevel%
echo.
pause
exit /b %rc%
