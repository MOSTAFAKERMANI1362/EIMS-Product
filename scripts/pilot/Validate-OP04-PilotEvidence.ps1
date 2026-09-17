param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolved = Resolve-Path $EvidencePath -ErrorAction Stop

Write-Host "EIMS OP-04 Pilot Evidence Validation"
Write-Host "Evidence file: $($resolved.Path)"
Write-Host "Note: validator prints gate status/missing field paths only; do not place credentials or PII in the evidence JSON."

Push-Location $repoRoot
try {
    dotnet run --project .\src\EIMS.PilotEnvironment.Readiness\EIMS.PilotEnvironment.Readiness.csproj -c Release -- $resolved.Path
    $code = $LASTEXITCODE
    switch ($code) {
        0 { Write-Host "OP04 RESULT: PILOT_ACTIVATION_READY" }
        3 { Write-Host "OP04 RESULT: BLOCKED_EVIDENCE_REQUIRED" }
        2 { Write-Host "OP04 RESULT: INVALID_EVIDENCE_INPUT" }
        default { Write-Host "OP04 RESULT: VALIDATOR_ERROR_$code" }
    }
    exit $code
}
finally {
    Pop-Location
}
