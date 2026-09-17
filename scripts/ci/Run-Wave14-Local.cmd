@echo off
setlocal
cd /d "%~dp0\..\.."
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\scripts\ci\Run-Wave14-Local.ps1"
exit /b %ERRORLEVEL%
