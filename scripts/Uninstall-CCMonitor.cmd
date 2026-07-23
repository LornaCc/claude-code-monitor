@echo off
PowerShell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-CCMonitor.ps1"
if errorlevel 1 pause
