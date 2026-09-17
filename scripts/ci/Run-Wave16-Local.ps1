param([string]$ReportPath = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-ci\Wave16-$stamp.log"
}
New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null

function Section([string]$title) {
    Write-Host ""
    Write-Host "============================================================"
    Write-Host $title
    Write-Host "============================================================"
}

function Checked([string]$name, [scriptblock]$command) {
    Section $name
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
    Write-Host "PASS: $name"
}

Start-Transcript -Path $ReportPath -Force | Out-Null
try {
    Section "EIMS Wave16 Physical Pilot Readiness Pack Local Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: WAVE16-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet CLI unavailable" }
    Write-Host "DotNetSdk: $sdk"
    if (-not $sdk.StartsWith("10.")) { throw "Wave16 requires .NET SDK 10.x. Found: $sdk" }

    Checked "Build Pilot Host" { dotnet build .\src\EIMS.PilotAssembly.Host\EIMS.PilotAssembly.Host.csproj -c Release }
    Checked "Run Wave15 activation contract tests" { dotnet run --project .\tests\EIMS.P1P5.Wave15HostActivation.ContractTests\EIMS.P1P5.Wave15HostActivation.ContractTests.csproj -c Release }
    Checked "Run authoritative OP04 environment regression" { dotnet run --project .\tests\EIMS.OP04.EnvironmentReadiness.ContractTests\EIMS.OP04.EnvironmentReadiness.ContractTests.csproj -c Release }

    Section "Verify sanitized PILOT template remains fail-closed"
    $template = Join-Path $repoRoot "deployment\pilot\OP04_PILOT_ENVIRONMENT_EVIDENCE_TEMPLATE_v1.1.json"
    dotnet run --project .\src\EIMS.PilotEnvironment.Readiness\EIMS.PilotEnvironment.Readiness.csproj -c Release -- $template
    $templateExit = $LASTEXITCODE
    if ($templateExit -ne 3) {
        throw "Sanitized PILOT template must be blocked with exit code 3, actual: $templateExit"
    }
    Write-Host "PASS: sanitized PILOT template is BLOCKED_EVIDENCE_REQUIRED"

    Section "FINAL RESULT"
    Write-Host "PASS: WAVE16 READINESS PACK LOCAL VALIDATION"
    Write-Host "Wave15 activation controls: 8"
    Write-Host "Authoritative OP04 controls: 10"
    Write-Host "Fail-closed template control: 1"
    Write-Host "Expected combined Wave16 checks: 19"
    Write-Host "Physical Pilot claim: NOT READY; REAL ENVIRONMENT EVIDENCE REQUIRED"
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    Write-Host "Report: $ReportPath"
    exit 0
}
catch {
    Section "FINAL RESULT"
    Write-Host "FAIL: WAVE16 READINESS PACK LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
