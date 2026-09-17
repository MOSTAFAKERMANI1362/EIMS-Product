@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Wave16-Local.ps1" %*
exit /b %ERRORLEVEL%
