@echo off
setlocal
if "%~1"=="" (
  echo Usage: Validate-OP04-PilotEvidence.cmd ^<evidence-json-path^>
  exit /b 2
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Validate-OP04-PilotEvidence.ps1" -EvidencePath "%~1"
exit /b %ERRORLEVEL%
