@echo off
setlocal
set "PORT=%~1"
if "%PORT%"=="" set "PORT=4311"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-companion.ps1" -Port %PORT%
