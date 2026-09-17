$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot
$reportDir = Join-Path $repoRoot "artifacts\local-ci"
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$report = Join-Path $reportDir "Wave17A-Package-$stamp.log"

function Assert-True([bool]$value, [string]$message) { if (-not $value) { throw $message } }
function Pass([string]$message) { Write-Host "PASS: $message" }

Start-Transcript -Path $report -Force | Out-Null
try {
    Write-Host "============================================================"
    Write-Host "EIMS Wave17A Pilot Deployment Package Local Validation"
    Write-Host "============================================================"
    $sdk = (& dotnet --version).Trim()
    Assert-True ($LASTEXITCODE -eq 0) "dotnet CLI unavailable"
    Assert-True ($sdk.StartsWith("10.")) "Expected .NET SDK 10.x. Found: $sdk"
    Write-Host "DotNetSdk: $sdk"

    $dist = Join-Path $repoRoot "artifacts\distribution"
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    & ".\scripts\deployment\Build-Pilot-TechnicalValidation-Package.ps1" -OutputDirectory $dist
    Assert-True ($LASTEXITCODE -eq 0) "Package builder failed"
    Pass "package builder completed"

    $zip = Get-ChildItem $dist -Filter "EIMS_Pilot_TechnicalValidation_*.zip" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Assert-True ($null -ne $zip) "Package ZIP not found"
    Assert-True (Test-Path ($zip.FullName + ".sha256")) "ZIP SHA256 sidecar missing"
    Pass "ZIP and sidecar hash exist"

    $expectedZipHash = ((Get-Content ($zip.FullName + ".sha256") -Raw).Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]).Trim().ToLowerInvariant()
    $actualZipHash = (Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($expectedZipHash -eq $actualZipHash) "ZIP SHA256 mismatch"
    Pass "ZIP SHA256 matches"

    $extract = Join-Path $repoRoot "artifacts\package-verify\$stamp"
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $zip.FullName -DestinationPath $extract -Force

    Assert-True (Test-Path (Join-Path $extract "app\EIMS.PilotAssembly.Host.dll")) "Published Host DLL missing"
    Assert-True (Test-Path (Join-Path $extract "docs\PILOT_TECHNICAL_VALIDATION_PACKAGE_README.md")) "Package README missing"
    Assert-True (Test-Path (Join-Path $extract "docs\OP04_PILOT_ENVIRONMENT_EVIDENCE_TEMPLATE_v1.1.json")) "OP04 template missing"
    Assert-True (Test-Path (Join-Path $extract "PACKAGE_METADATA.json")) "Package metadata missing"
    Assert-True (Test-Path (Join-Path $extract "SHA256SUMS.txt")) "File hash manifest missing"
    Pass "required package contents exist"

    $metadata = Get-Content (Join-Path $extract "PACKAGE_METADATA.json") -Raw | ConvertFrom-Json
    Assert-True ($metadata.packagePurpose -eq "ISOLATED_TECHNICAL_VALIDATION_ONLY") "Package purpose mismatch"
    Assert-True ($metadata.defaultActivationState -eq "FAIL_CLOSED_UNTIL_OP04_AND_PHYSICAL_COMPOSITION_READY") "Default activation state mismatch"
    Assert-True (-not $metadata.secretsIncluded) "Metadata indicates secrets included"
    Assert-True (-not $metadata.personalDataIncluded) "Metadata indicates personal data included"
    Pass "metadata confirms isolated fail-closed package"

    Assert-True (-not (Test-Path (Join-Path $extract "app\appsettings.Pilot.example.json"))) "Example Pilot config must not be active in app directory"
    Assert-True (Test-Path (Join-Path $extract "docs\appsettings.Pilot.example.json")) "Example Pilot config missing from docs"
    Pass "non-secret configuration remains documentation-only"

    $manifestLines = Get-Content (Join-Path $extract "SHA256SUMS.txt") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    Assert-True ($manifestLines.Count -gt 5) "Hash manifest unexpectedly small"
    Pass "SHA256 file manifest populated"

    & dotnet run --project ".\tests\EIMS.P1P5.Wave15HostActivation.ContractTests\EIMS.P1P5.Wave15HostActivation.ContractTests.csproj" -c Release
    Assert-True ($LASTEXITCODE -eq 0) "Wave15 activation regression failed"
    Pass "Wave15 fail-closed activation regression"

    & dotnet run --project ".\tests\EIMS.OP04.EnvironmentReadiness.ContractTests\EIMS.OP04.EnvironmentReadiness.ContractTests.csproj" -c Release
    Assert-True ($LASTEXITCODE -eq 0) "OP04 regression failed"
    Pass "authoritative OP04 regression"

    Write-Host "============================================================"
    Write-Host "PASS: WAVE17A PILOT DEPLOYMENT PACKAGE LOCAL VALIDATION"
    Write-Host "Package: $($zip.FullName)"
    Write-Host "PackageSHA256: $actualZipHash"
    Write-Host "Deployment claim: ISOLATED TECHNICAL VALIDATION ONLY"
    Write-Host "Physical Pilot activation claim: NOT CLAIMED"
    Write-Host "Report: $report"
    exit 0
}
catch {
    Write-Host "============================================================"
    Write-Host "FAIL: WAVE17A PILOT DEPLOYMENT PACKAGE LOCAL VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $report"
    exit 1
}
finally { Stop-Transcript | Out-Null }
