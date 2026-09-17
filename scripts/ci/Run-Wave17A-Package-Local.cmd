@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Wave17A-Package-Local.ps1" %*
exit /b %ERRORLEVEL%
