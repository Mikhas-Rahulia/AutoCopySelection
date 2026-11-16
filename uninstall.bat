@echo off
setlocal
cd /d "%~dp0"

echo Stopping AutoCopySelection and removing autostart...
if exist "%~dp0AutoCopySelection.exe" (
    "%~dp0AutoCopySelection.exe" --uninstall
)
schtasks /End /TN "AutoCopySelectionTask" >nul 2>&1
schtasks /Delete /TN "AutoCopySelectionTask" /F >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "AutoCopySelection" /f >nul 2>&1
echo AutoCopySelection has been stopped and removed from startup.
pause
