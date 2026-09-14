param(
  [string]$OutputPath = ".\\windows-pilot-evidence.json"
)

$ErrorActionPreference = 'Stop'

$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem

$dotnet = ""
try {
  $dotnet = (& dotnet --list-runtimes 2>$null | Out-String).Trim()
} catch {
  $dotnet = "dotnet runtime not detected"
}

$iis = Get-WindowsFeature Web-Server -ErrorAction SilentlyContinue
$wa  = Get-WindowsFeature Web-Windows-Auth -ErrorAction SilentlyContinue

$data = [ordered]@{
  serverName = $env:COMPUTERNAME
  osVersion = "$($os.Caption) $($os.Version)"
  vmProvisioned = $true
  domainJoined = [bool]$cs.PartOfDomain
  domainName = [string]$cs.Domain
  iisInstalled = [bool]($iis -and $iis.Installed)
  windowsAuthenticationInstalled = [bool]($wa -and $wa.Installed)
  dotnetRuntimeVersion = $dotnet
  collectedAt = (Get-Date).ToString('o')
  collectedBy = "$env:USERDOMAIN\\$env:USERNAME"
}

$data | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $OutputPath

Write-Host "Written: $OutputPath"
Write-Host "This collector does not read passwords, connection strings, tokens or private keys."
Write-Host "Review the output before sharing. Do not commit sensitive or personal data."
