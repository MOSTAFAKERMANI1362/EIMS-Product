param([string]$ReportPath = "")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-ci\Wave14-$stamp.log"
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
    Section "EIMS P1-P5 Wave14 Knowledge Binding Local Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: WAVE14-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet CLI is not available." }
    Write-Host "DotNetSdk: $sdk"
    if (-not $sdk.StartsWith("10.")) { throw "EIMS Wave14 requires .NET SDK 10.x. Found: $sdk" }

    Checked "Build Pilot Host with Wave14 binding source" {
        dotnet build .\src\EIMS.PilotAssembly.Host\EIMS.PilotAssembly.Host.csproj -c Release
    }
    Checked "Build Wave14 Knowledge binding tests" {
        dotnet build .\tests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests.csproj -c Release
    }
    Checked "Run Wave14 Knowledge binding tests" {
        dotnet run --project .\tests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests\EIMS.P1P5.Wave14KnowledgeBinding.ContractTests.csproj -c Release --no-build
    }
    Checked "Build Wave14 real Knowledge composition tests" {
        dotnet build .\tests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests.csproj -c Release
    }
    Checked "Run Wave14 real Knowledge composition tests" {
        dotnet run --project .\tests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests\EIMS.P1P5.Wave14KnowledgeComposition.ContractTests.csproj -c Release --no-build
    }

    Checked "Historical P1-P5 binding regression" {
        dotnet run --project .\tests\EIMS.P1P5.RuntimeBinding.ContractTests\EIMS.P1P5.RuntimeBinding.ContractTests.csproj -c Release
    }
    Checked "Wave12 real Execution Benefit composition regression" {
        dotnet run --project .\tests\EIMS.P1P5.Wave12Composition.ContractTests\EIMS.P1P5.Wave12Composition.ContractTests.csproj -c Release
    }
    Checked "Wave10 Execution lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.Execution.ContractTests\EIMS.P1.Wave10.Execution.ContractTests.csproj -c Release
    }
    Checked "Wave10 Execution intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave10.ExecutionIntake.ContractTests\EIMS.P1.Wave10.ExecutionIntake.ContractTests.csproj -c Release
    }
    Checked "Wave11 Benefit lifecycle regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.Benefit.ContractTests\EIMS.P1.Wave11.Benefit.ContractTests.csproj -c Release
    }
    Checked "Wave11 Benefit intake regression" {
        dotnet run --project .\tests\EIMS.P1.Wave11.BenefitIntake.ContractTests\EIMS.P1.Wave11.BenefitIntake.ContractTests.csproj -c Release
    }
    Checked "Wave13 Knowledge runtime regression" {
        dotnet run --project .\tests\EIMS.P1.Wave13.Knowledge.ContractTests\EIMS.P1.Wave13.Knowledge.ContractTests.csproj -c Release
    }
    Checked "Verify ACR-P0-008" {
        dotnet run --project .\tools\EIMS.ACR.P0.008.Verifier\EIMS.ACR.P0.008.Verifier.csproj -c Release -- .\architecture\decisions\ACR-P0-008_POST_G04_COMMAND_BINDING_v1.0.json
    }

    Section "FINAL RESULT"
    Write-Host "PASS: WAVE14 LOCAL VALIDATION"
    Write-Host "Binding contract: P1P5-1.3.0"
    Write-Host "Recovered user mutations: 32"
    Write-Host "Dedicated Wave14 binding tests: 16"
    Write-Host "Real Wave14 Knowledge composition tests: 4"
    Write-Host "Expected combined control/test evidence: 177 checks"
    Write-Host "Note: version-locked Wave9/Wave12 mapping suites are superseded here by the consolidated Wave14 mapping regression; Wave12 real composition remains executed."
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    Write-Host "Report: $ReportPath"
    exit 0
}
catch {
    Section "FINAL RESULT"
    Write-Host "FAIL: WAVE14 LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
