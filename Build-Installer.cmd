@echo off
setlocal
where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 is required. Run Build-Installer.ps1 from PowerShell 7.
  pause
  exit /b 1
)
pwsh.exe -NoProfile -File "%~dp0Build-Installer.ps1" %*
if errorlevel 1 (
  echo.
  echo Build failed. See the log in artifacts.
  pause
  exit /b 1
)
echo.
pause
