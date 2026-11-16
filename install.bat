@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Compiling AutoCopySelection.cs with built-in .NET Framework csc.exe...
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"

if exist "%CSC%" (
    "%CSC%" /nologo /optimize+ /target:winexe /out:"%~dp0AutoCopySelection_new.exe" /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" "%~dp0AutoCopySelection.cs"
    if exist "%~dp0AutoCopySelection_new.exe" (
        if exist "%~dp0AutoCopySelection.exe" move /y "%~dp0AutoCopySelection.exe" "%~dp0AutoCopySelection.old.exe" >nul 2>&1
        move /y "%~dp0AutoCopySelection_new.exe" "%~dp0AutoCopySelection.exe" >nul 2>&1
    )
)

echo [2/3] Registering persistent autostart (Task Scheduler + HKCU Run)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$exe = Join-Path '%~dp0' 'AutoCopySelection.exe'; " ^
  "try { " ^
  "  $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory '%~dp0'; " ^
  "  $trigger = New-ScheduledTaskTrigger -AtLogOn; " ^
  "  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1); " ^
  "  $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest; " ^
  "  Register-ScheduledTask -TaskName 'AutoCopySelectionTask' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force -ErrorAction Stop | Out-Null; " ^
  "  Start-ScheduledTask -TaskName 'AutoCopySelectionTask'; " ^
  "  Write-Host 'Registered Scheduled Task AutoCopySelectionTask (Admin/Highest).' -ForegroundColor Green; " ^
  "} catch { " ^
  "  Start-Process -FilePath $exe; " ^
  "  Write-Host 'Started AutoCopySelection.exe with Registry HKCU Run autostart (Standard User).' -ForegroundColor Yellow; " ^
  "}"

echo [3/3] Done! AutoCopySelection is active in the background.
pause
