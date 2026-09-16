param(
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-ci\Wave10-$stamp.log"
}

$reportDir = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "============================================================"
    Write-Host $title
    Write-Host "============================================================"
}

function Invoke-Checked([string]$name, [scriptblock]$command) {
    Write-Section $name
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE"
    }
    Write-Host "PASS: $name"
}

Start-Transcript -Path $ReportPath -Force | Out-Null
try {
    Write-Section "EIMS Wave10 Local Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: WAVE10-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"

    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet CLI is not available." }
    Write-Host "DotNetSdk: $sdk"
    if (-not $sdk.StartsWith("10.")) {
        throw "EIMS Wave10 requires .NET SDK 10.x. Found: $sdk"
    }

    Invoke-Checked "Build Wave10 Execution lifecycle tests" {
        dotnet build .\tests\EIMS.P1.Wave10.Execution.ContractTests\EIMS.P1.Wave10.Execution.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave10 Execution lifecycle tests" {
        dotnet run --project .\tests\EIMS.P1.Wave10.Execution.ContractTests\EIMS.P1.Wave10.Execution.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Build Wave10 Execution handoff intake tests" {
        dotnet build .\tests\EIMS.P1.Wave10.ExecutionIntake.ContractTests\EIMS.P1.Wave10.ExecutionIntake.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave10 Execution handoff intake tests" {
        dotnet run --project .\tests\EIMS.P1.Wave10.ExecutionIntake.ContractTests\EIMS.P1.Wave10.ExecutionIntake.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Build ACR-P0-008 verifier" {
        dotnet build .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release
    }
    Invoke-Checked "Verify ACR-P0-008" {
        dotnet run --project .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release --no-build -- .\architecture\decisions\ACR-P0-008_POST_G04_COMMAND_BINDING_v1.0.json
    }

    Invoke-Checked "Build Wave9 Portfolio regression" {
        dotnet build .\tests\EIMS.P1.Wave9.Portfolio.ContractTests\EIMS.P1.Wave9.Portfolio.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave9 Portfolio regression" {
        dotnet run --project .\tests\EIMS.P1.Wave9.Portfolio.ContractTests\EIMS.P1.Wave9.Portfolio.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Build P1-P5 Wave9 binding regression" {
        dotnet build .\tests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run P1-P5 Wave9 binding regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests.csproj -c Release --no-build
    }

    Write-Section "FINAL RESULT"
    Write-Host "PASS: WAVE10 LOCAL VALIDATION"
    Write-Host "Expected dedicated Wave10 coverage: 30 lifecycle + 8 intake/hardening = 38 tests"
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    Write-Host "Report: $ReportPath"
    exit 0
}
catch {
    Write-Section "FINAL RESULT"
    Write-Host "FAIL: WAVE10 LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
