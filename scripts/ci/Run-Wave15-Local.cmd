@echo off
setlocal
set SCRIPT_DIR=%~dp0
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Run-Wave15-Local.ps1"
set EXITCODE=%ERRORLEVEL%
endlocal & exit /b %EXITCODE%
