param([string]$ReportPath = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-ci\Wave15-$stamp.log"
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
    Section "EIMS P5 Wave15 Controlled Runtime Composition Local Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: WAVE15-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet CLI is not available." }
    Write-Host "DotNetSdk: $sdk"
    if (-not $sdk.StartsWith("10.")) { throw "EIMS Wave15 requires .NET SDK 10.x. Found: $sdk" }

    Checked "Build Pilot Host with Wave15 activation seam" {
        dotnet build .\src\EIMS.PilotAssembly.Host\EIMS.PilotAssembly.Host.csproj -c Release
    }
    Checked "Run Wave15 runtime composition contracts" {
        dotnet run --project .\tests\EIMS.P5.Wave15.RuntimeComposition.ContractTests\EIMS.P5.Wave15.RuntimeComposition.ContractTests.csproj -c Release
    }
    Checked "Run P5 Pilot Assembly core regressions" {
        dotnet run --project .\tests\EIMS.PilotAssembly.ContractTests\EIMS.PilotAssembly.ContractTests.csproj -c Release
    }
    Checked "Run OP04 environment readiness regressions" {
        dotnet run --project .\tests\EIMS.OP04.EnvironmentReadiness.ContractTests\EIMS.OP04.EnvironmentReadiness.ContractTests.csproj -c Release
    }

    Checked "Run Wave14 consolidated Knowledge binding regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests.csproj -c Release
    }
    Checked "Run Wave14 real Knowledge composition regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests.csproj -c Release
    }
    Checked "Run historical P1-P5 binding regression" {
        dotnet run --project .\tests\EIMS.P1P5.RuntimeBinding.ContractTests\EIMS.P1P5.RuntimeBinding.ContractTests.csproj -c Release
    }
    Checked "Run Wave12 real Execution Benefit composition regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave12Composition.ContractTests\EIMS.P1P5.Wave12Composition.ContractTests.csproj -c Release
    }
    Checked "Run Wave10 Execution lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.Execution.ContractTests\EIMS.P1.Wave10.Execution.ContractTests.csproj -c Release
    }
    Checked "Run Wave10 Execution intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.ExecutionIntake.ContractTests\EIMS.P1.Wave10.ExecutionIntake.ContractTests.csproj -c Release
    }
    Checked "Run Wave11 Benefit lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.Benefit.ContractTests\EIMS.P1.Wave11.Benefit.ContractTests.csproj -c Release
    }
    Checked "Run Wave11 Benefit intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.BenefitIntake.ContractTests\EIMS.P1.Wave11.BenefitIntake.ContractTests.csproj -c Release
    }
    Checked "Run Wave13 Knowledge runtime regression" {
        dotnet run --project .\tests\EIMS.P1.Wave13.Knowledge.ContractTests\EIMS.P1.Wave13.Knowledge.ContractTests.csproj -c Release
    }
    Checked "Verify ACR-P0-008" {
        dotnet run --project .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release -- .\architecture\decisions\ACR-P0-008_POST_G04_COMMAND_BINDING_v1.0.json
    }

    Section "FINAL RESULT"
    Write-Host "PASS: WAVE15 LOCAL VALIDATION"
    Write-Host "Host production default: FAIL_CLOSED unless physical P2 + live P3 + clean OP04 PILOT evidence are supplied"
    Write-Host "P1-P5 binding contract: P1P5-1.3.0 / 32 recovered user mutations"
    Write-Host "Dedicated Wave15 activation tests: 15"
    Write-Host "P5 core readiness regressions: 15"
    Write-Host "OP04 readiness regressions: 10"
    Write-Host "Prior Wave14 regression evidence rerun: 177"
    Write-Host "Expected combined control/test evidence: 217 checks"
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    Write-Host "Report: $ReportPath"
    exit 0
}
catch {
    Section "FINAL RESULT"
    Write-Host "FAIL: WAVE15 LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
