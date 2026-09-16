param(
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ReportPath = Join-Path $repoRoot "artifacts\local-security\Security-$stamp.log"
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
    Write-Section "EIMS Local Security Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: SECURITY-LOCAL-1.0"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        Invoke-Checked "Repository policy" { python .\scripts\ci\repository_policy_check.py }
    }
    else {
        $py = Get-Command py -ErrorAction SilentlyContinue
        if (-not $py) { throw "Python 3 is required for repository policy validation." }
        Invoke-Checked "Repository policy" { py -3 .\scripts\ci\repository_policy_check.py }
    }

    $version = "0.71.0"
    $zipName = "trivy_${version}_windows-64bit.zip"
    $expectedSha = "382250158fb9431ff9b87904205027b066a544234b8952b2dd764bd712d55387"
    $toolsRoot = Join-Path $repoRoot "artifacts\local-tools"
    $trivyDir = Join-Path $toolsRoot "trivy-$version"
    $zipPath = Join-Path $toolsRoot $zipName
    $trivyExe = Join-Path $trivyDir "trivy.exe"
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

    if (-not (Test-Path $trivyExe)) {
        Write-Section "Download Trivy v$version"
        $uri = "https://github.com/aquasecurity/trivy/releases/download/v$version/$zipName"
        Invoke-WebRequest -Uri $uri -OutFile $zipPath -UseBasicParsing
        $actualSha = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "TrivyZipSha256: $actualSha"
        if ($actualSha -ne $expectedSha) { throw "Trivy checksum mismatch." }
        if (Test-Path $trivyDir) { Remove-Item $trivyDir -Recurse -Force }
        New-Item -ItemType Directory -Path $trivyDir -Force | Out-Null
        Expand-Archive -Path $zipPath -DestinationPath $trivyDir -Force
    }

    Invoke-Checked "Trivy version" { & $trivyExe version }
    Invoke-Checked "Trivy HIGH/CRITICAL vulnerability-secret-misconfig scan" {
        & $trivyExe fs --scanners vuln,secret,misconfig --severity HIGH,CRITICAL --exit-code 1 --no-progress .
    }

    $sbom = Join-Path $reportDir "eims-sbom.cdx.json"
    Invoke-Checked "Generate CycloneDX SBOM" {
        & $trivyExe fs --format cyclonedx --output $sbom --no-progress .
    }

    Write-Section "FINAL RESULT"
    Write-Host "PASS: EIMS LOCAL SECURITY VALIDATION"
    Write-Host "Report: $ReportPath"
    Write-Host "SBOM: $sbom"
    Write-Host "CompletedAt: $(Get-Date -Format o)"
    exit 0
}
catch {
    Write-Section "FINAL RESULT"
    Write-Host "FAIL: EIMS LOCAL SECURITY VALIDATION"
    Write-Host $_.Exception.Message
    Write-Host "Report: $ReportPath"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
