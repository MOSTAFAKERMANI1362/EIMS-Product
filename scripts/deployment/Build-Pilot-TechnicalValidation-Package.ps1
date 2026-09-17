param(
    [string]$OutputDirectory = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

function Fail([string]$message) { throw $message }

$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { Fail "dotnet CLI is unavailable." }
if (-not $sdk.StartsWith("10.")) { Fail "EIMS Pilot package requires .NET SDK 10.x. Found: $sdk" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$packageName = "EIMS_Pilot_TechnicalValidation_$stamp"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\distribution"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$workRoot = Join-Path $repoRoot "artifacts\package-work\$packageName"
$appDir = Join-Path $workRoot "app"
$docsDir = Join-Path $workRoot "docs"
Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $docsDir -Force | Out-Null

Write-Host "Publishing EIMS Pilot Host..."
& dotnet publish ".\src\EIMS.PilotAssembly.Host\EIMS.PilotAssembly.Host.csproj" `
    -c $Configuration `
    --no-self-contained `
    -o $appDir `
    /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed with exit code $LASTEXITCODE" }

$requiredDocs = @(
    "deployment\pilot\OP04_PILOT_ENVIRONMENT_EVIDENCE_TEMPLATE_v1.1.json",
    "deployment\pilot\WAVE16_PHYSICAL_PILOT_READINESS_RUNBOOK.md",
    "deployment\pilot\WAVE16_PRODUCTION_COMPOSITION_BINDING_PLAN.md",
    "deployment\pilot\WAVE16_RESPONSIBILITY_MATRIX.md",
    "deployment\pilot\PILOT_TECHNICAL_VALIDATION_PACKAGE_README.md",
    "deployment\pilot\PILOT_DEPLOYMENT_AND_ROLLBACK_CHECKLIST.md",
    "deployment\pilot\appsettings.Pilot.example.json"
)
foreach ($relative in $requiredDocs) {
    $source = Join-Path $repoRoot $relative
    if (-not (Test-Path $source)) { Fail "Required package document not found: $relative" }
    Copy-Item $source -Destination (Join-Path $docsDir (Split-Path $relative -Leaf)) -Force
}

$sourceCommit = "UNAVAILABLE_FROM_SOURCE_ARCHIVE"
try {
    $candidate = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($candidate)) { $sourceCommit = $candidate }
} catch { }

$metadata = [ordered]@{
    packageName = $packageName
    packagePurpose = "ISOLATED_TECHNICAL_VALIDATION_ONLY"
    builtAt = (Get-Date -Format o)
    dotnetSdk = $sdk
    configuration = $Configuration
    sourceCommit = $sourceCommit
    selfContained = $false
    installerType = "TRANSPARENT_ZIP_NO_MACHINE_LEVEL_INSTALLER"
    defaultActivationState = "FAIL_CLOSED_UNTIL_OP04_AND_PHYSICAL_COMPOSITION_READY"
    secretsIncluded = $false
    personalDataIncluded = $false
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $workRoot "PACKAGE_METADATA.json") -Encoding UTF8

$notice = @"
EIMS PILOT TECHNICAL VALIDATION PACKAGE

Purpose: isolated technical validation only.
This package is NOT a production Go-Live installer.
It contains no database password, connection string, token, PFX/private key or HR/customer personal data.
The EIMS Host remains fail-closed until authoritative OP-04 PILOT evidence and a validated physical runtime composition are both present.

Server prerequisite: IT-approved Windows Server VM with IIS/Windows Authentication and the approved .NET 10 ASP.NET Core Hosting Bundle/runtime prerequisites.
See docs\PILOT_TECHNICAL_VALIDATION_PACKAGE_README.md before deployment.
"@
$notice | Set-Content -Path (Join-Path $workRoot "READ_FIRST.txt") -Encoding UTF8

$hashLines = New-Object System.Collections.Generic.List[string]
Get-ChildItem $workRoot -File -Recurse | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object FullName | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $_.FullName.Substring($workRoot.Length).TrimStart('\').Replace('\','/')
    $hashLines.Add("$hash  $relative")
}
$hashLines | Set-Content -Path (Join-Path $workRoot "SHA256SUMS.txt") -Encoding ASCII

$zipPath = Join-Path $OutputDirectory "$packageName.zip"
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $workRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path $zipPath -Leaf)" | Set-Content -Path "$zipPath.sha256" -Encoding ASCII

Write-Host ""
Write-Host "PASS: EIMS Pilot Technical Validation package created"
Write-Host "Package: $zipPath"
Write-Host "SHA256 : $zipHash"
Write-Host "Purpose: ISOLATED TECHNICAL VALIDATION ONLY"
Write-Host "Activation: FAIL-CLOSED BY DEFAULT"
