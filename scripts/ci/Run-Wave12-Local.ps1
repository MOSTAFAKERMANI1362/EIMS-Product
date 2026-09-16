param(
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-ci\Wave12-$stamp.log"
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
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
    Write-Host "PASS: $name"
}

Start-Transcript -Path $ReportPath -Force | Out-Null
try {
    Write-Section "EIMS P1-P5 Wave12 Local Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: WAVE12-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"

    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet CLI is not available." }
    Write-Host "DotNetSdk: $sdk"
    if (-not $sdk.StartsWith("10.")) { throw "EIMS Wave12 requires .NET SDK 10.x. Found: $sdk" }

    Invoke-Checked "Build Pilot Host with Wave12 binding" {
        dotnet build .\src\EIMS.PilotAssembly.Host\EIMS.PilotAssembly.Host.csproj -c Release
    }

    Invoke-Checked "Build historical P1-P5 runtime binding tests" {
        dotnet build .\tests\EIMS.P1P5.RuntimeBinding.ContractTests\EIMS.P1P5.RuntimeBinding.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run historical P1-P5 runtime binding tests" {
        dotnet run --project .\tests\EIMS.P1P5.RuntimeBinding.ContractTests\EIMS.P1P5.RuntimeBinding.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Build Wave9 Portfolio P1-P5 regression" {
        dotnet build .\tests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave9 Portfolio P1-P5 regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests\EIMS.P1P5.Wave9PortfolioBinding.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Build Wave12 Execution Benefit P1-P5 binding tests" {
        dotnet build .\tests\EIMS.P1P5.Wave12ExecutionBenefitBinding.ContractTests\EIMS.P1P5.Wave12ExecutionBenefitBinding.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave12 Execution Benefit P1-P5 binding tests" {
        dotnet run --project .\tests\EIMS.P1P5.Wave12ExecutionBenefitBinding.ContractTests\EIMS.P1P5.Wave12ExecutionBenefitBinding.ContractTests.csproj -c Release --no-build
    }

    Invoke-Checked "Run Wave10 Execution lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.Execution.ContractTests\EIMS.P1.Wave10.Execution.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave10 Execution intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.ExecutionIntake.ContractTests\EIMS.P1.Wave10.ExecutionIntake.ContractTests.csproj -c Release
    }

    Invoke-Checked "Run Wave11 Benefit lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.Benefit.ContractTests\EIMS.P1.Wave11.Benefit.ContractTests.csproj -c Release
    }
    Invoke-Checked "Run Wave11 Benefit intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.BenefitIntake.ContractTests\EIMS.P1.Wave11.BenefitIntake.ContractTests.csproj -c Release
    }

    Invoke-Checked "Build ACR-P0-008 verifier" {
        dotnet build .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release
    }
    Invoke-Checked "Verify ACR-P0-008" {
        dotnet run --project .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release --no-build -- .\architecture\decisions\ACR-P0-008_POST_G04_COMMAND_BINDING_v1.0.json
    }

    Write-Section "FINAL RESULT"
    Write-Host "PASS: WAVE12 LOCAL VALIDATION"
    Write-Host "Binding contract: P1P5-1.2.0"
    Write-Host "Recovered user mutations: 29"
    Write-Host "Dedicated Wave12 binding tests: 19"
    Write-Host "Expected combined control/test evidence: 160 checks across historical binding, Wave9, Wave10, Wave11 and ACR-P0-008 suites"
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    Write-Host "Report: $ReportPath"
    exit 0
}
catch {
    Write-Section "FINAL RESULT"
    Write-Host "FAIL: WAVE12 LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
