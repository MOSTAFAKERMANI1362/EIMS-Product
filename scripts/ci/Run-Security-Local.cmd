@echo off
setlocal
cd /d "%~dp0\..\.."
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\scripts\ci\Run-Security-Local.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if "%EXITCODE%"=="0" (
  echo EIMS local security validation PASSED.
) else (
  echo EIMS local security validation FAILED with exit code %EXITCODE%.
)
echo.
pause
exit /b %EXITCODE%
