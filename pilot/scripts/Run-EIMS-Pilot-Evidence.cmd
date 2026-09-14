@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "COLLECTOR=%SCRIPT_DIR%Collect-EimsPilotEnvironmentEvidence.ps1"
set "OUTPUT=%SCRIPT_DIR%eims-pilot-environment-evidence.json"

if not exist "%COLLECTOR%" (
  echo ERROR: Evidence collector not found next to this runner.
  echo Expected: %COLLECTOR%
  goto :fail
)

set "PS_EXE="
where powershell.exe >nul 2>&1 && set "PS_EXE=powershell.exe"
if not defined PS_EXE where pwsh.exe >nul 2>&1 && set "PS_EXE=pwsh.exe"

if not defined PS_EXE (
  echo ERROR: Windows PowerShell or PowerShell 7 was not found.
  goto :fail
)

echo Collecting EIMS PILOT environment evidence...
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%COLLECTOR%" -EvidenceClass PILOT_ENVIRONMENT_EVIDENCE -OutputPath "%OUTPUT%"
if errorlevel 1 goto :fail
if not exist "%OUTPUT%" (
  echo ERROR: Evidence file was not created.
  goto :fail
)

echo.
echo SUCCESS: Evidence file created:
echo %OUTPUT%
echo Review the JSON, then upload only that JSON file back to ChatGPT.
echo Optional Oracle TCP probing is intentionally NOT performed by this one-click runner.
if not "%EIMS_NO_PAUSE%"=="1" pause
exit /b 0

:fail
echo.
echo EIMS PILOT evidence collection FAILED. No readiness claim was made.
if not "%EIMS_NO_PAUSE%"=="1" pause
exit /b 1
