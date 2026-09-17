@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Pilot-TechnicalValidation-Package.ps1" %*
exit /b %ERRORLEVEL%
