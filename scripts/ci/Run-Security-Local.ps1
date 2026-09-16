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

function Invoke-NativeChecked([string]$name, [scriptblock]$command) {
    Write-Section $name
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
    Write-Host "PASS: $name"
}

function Invoke-RepositoryPolicy {
    Write-Section "Repository policy"

    $forbiddenSuffixes = @('.pfx', '.p12', '.key', '.pem', '.dmp', '.bak', '.dump', '.sqlite')
    $forbiddenPathParts = @('private-evidence', 'customer-data', 'secrets')
    $forbiddenPrefixes = @('hr-export', 'oracle-export')
    $violations = New-Object System.Collections.Generic.List[string]

    Get-ChildItem -Path $repoRoot -Recurse -File -Force | ForEach-Object {
        $full = $_.FullName
        $rel = $full.Substring($repoRoot.Length).TrimStart('\', '/')
        $parts = $rel -split '[\\/]'
        $partsLower = @($parts | ForEach-Object { $_.ToLowerInvariant() })
        $nameLower = $_.Name.ToLowerInvariant()
        $suffixLower = $_.Extension.ToLowerInvariant()

        if ($partsLower -contains '.git') { return }

        foreach ($part in $forbiddenPathParts) {
            if ($partsLower -contains $part) {
                $violations.Add("forbidden path: $rel")
                break
            }
        }

        if ($forbiddenSuffixes -contains $suffixLower) {
            $violations.Add("forbidden file type: $rel")
        }

        foreach ($prefix in $forbiddenPrefixes) {
            if ($nameLower.StartsWith($prefix)) {
                $violations.Add("forbidden data export filename: $rel")
                break
            }
        }
    }

    if ($violations.Count -gt 0) {
        Write-Host "EIMS repository policy check: FAIL"
        $violations | Sort-Object -Unique | ForEach-Object { Write-Host " - $_" }
        throw "Repository policy violations detected."
    }

    Write-Host "EIMS repository policy check: PASS"
    Write-Host "No prohibited evidence/data file paths were detected."
    Write-Host "PASS: Repository policy"
}

Start-Transcript -Path $ReportPath -Force | Out-Null
try {
    Write-Section "EIMS Local Security Validation"
    Write-Host "Runner: LOCAL-WINDOWS"
    Write-Host "ScriptVersion: SECURITY-LOCAL-1.2"
    Write-Host "RepositoryRoot: $repoRoot"
    Write-Host "StartedAt: $(Get-Date -Format o)"

    Invoke-RepositoryPolicy

    $version = "0.71.0"
    $zipName = "trivy_${version}_windows-64bit.zip"
    $expectedSha = "382250158fb9431ff9b87904205027b066a544234b8952b2dd764bd712d55387"
    $toolsRoot = Join-Path $repoRoot "artifacts\local-tools"
    $trivyDir = Join-Path $toolsRoot "trivy-$version"
    $zipPath = Join-Path $toolsRoot $zipName
    $trivyExe = Join-Path $trivyDir "trivy.exe"
    $trivyCache = Join-Path $toolsRoot "trivy-cache-ghcr"
    $dbRepository = "ghcr.io/aquasecurity/trivy-db:2"
    $checksRepository = "ghcr.io/aquasecurity/trivy-checks:1"
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $trivyCache -Force | Out-Null

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

    Invoke-NativeChecked "Trivy version" { & $trivyExe version }

    Invoke-NativeChecked "Download vulnerability DB from GHCR" {
        & $trivyExe image --cache-dir $trivyCache --db-repository $dbRepository --download-db-only --no-progress
    }

    Invoke-NativeChecked "Trivy HIGH/CRITICAL vulnerability-secret-misconfig scan" {
        & $trivyExe fs --cache-dir $trivyCache --db-repository $dbRepository --checks-bundle-repository $checksRepository --skip-db-update --scanners vuln,secret,misconfig --severity HIGH,CRITICAL --exit-code 1 --no-progress --skip-dirs artifacts .
    }

    $sbom = Join-Path $reportDir "eims-sbom.cdx.json"
    Invoke-NativeChecked "Generate CycloneDX SBOM" {
        & $trivyExe fs --cache-dir $trivyCache --db-repository $dbRepository --checks-bundle-repository $checksRepository --skip-db-update --format cyclonedx --output $sbom --no-progress --skip-dirs artifacts .
    }

    Write-Section "FINAL RESULT"
    Write-Host "PASS: EIMS LOCAL SECURITY VALIDATION"
    Write-Host "DB repository: $dbRepository"
    Write-Host "Checks repository: $checksRepository"
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
