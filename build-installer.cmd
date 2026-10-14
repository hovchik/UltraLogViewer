@echo off
rem Builds the UltraLogViewer Windows installer. Double-click it, or run it
rem from a terminal; extra arguments go to scripts\build-installer.ps1,
rem e.g.  build-installer.cmd -App Electron   or   build-installer.cmd -Version 1.0.0
rem Installs Inno Setup with winget if it is missing. Needs the .NET 8 SDK.
setlocal
cd /d "%~dp0"

where pwsh >nul 2>nul
if %errorlevel%==0 (set "PS=pwsh") else (set "PS=powershell")

%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-installer.ps1" -InstallTools %*
set "RC=%errorlevel%"

if %RC%==0 (
  echo.
  echo Installer is in "%~dp0release\installer"
  if exist "%~dp0release\installer" start "" explorer "%~dp0release\installer"
) else (
  echo.
  echo Build failed with exit code %RC%.
)

rem Keep the window open when started by double-click.
echo %cmdcmdline% | find /i "/c" >nul && pause
exit /b %RC%
