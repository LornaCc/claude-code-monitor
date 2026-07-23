@echo off
setlocal
PowerShell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CCMonitor.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. Keep this window open and send the error to the developer.
  pause
  exit /b 1
)
echo.
echo Installation completed successfully.
pause
