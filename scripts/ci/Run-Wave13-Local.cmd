@echo off
setlocal
cd /d "%~dp0\..\.."
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\scripts\ci\Run-Wave13-Local.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
echo Wave13 local validation exit code: %EXITCODE%
pause
exit /b %EXITCODE%
