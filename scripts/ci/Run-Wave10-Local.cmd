@echo off
setlocal
cd /d "%~dp0\..\.."
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\scripts\ci\Run-Wave10-Local.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if "%EXITCODE%"=="0" (
  echo EIMS Wave10 local validation PASSED.
) else (
  echo EIMS Wave10 local validation FAILED with exit code %EXITCODE%.
)
echo.
pause
exit /b %EXITCODE%
