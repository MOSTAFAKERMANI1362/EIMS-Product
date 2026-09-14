param(
  [string]$OutputPath = ".\windows-pilot-evidence.json"
)

$ErrorActionPreference = 'Stop'

function Get-AdminStatus {
  try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  } catch {
    return $false
  }
}

function Get-FeatureState {
  param(
    [string]$ServerFeatureName,
    [string]$ClientFeatureName
  )

  if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    try {
      $f = Get-WindowsFeature $ServerFeatureName -ErrorAction Stop
      return [ordered]@{
        source = 'Get-WindowsFeature'
        available = $true
        installed = [bool]$f.Installed
        state = [string]$f.InstallState
      }
    } catch {
      return [ordered]@{
        source = 'Get-WindowsFeature'
        available = $true
        installed = $false
        state = "QUERY_FAILED: $($_.Exception.Message)"
      }
    }
  }

  if (Get-Command Get-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
    try {
      $f = Get-WindowsOptionalFeature -Online -FeatureName $ClientFeatureName -ErrorAction Stop
      return [ordered]@{
        source = 'Get-WindowsOptionalFeature'
        available = $true
        installed = ($f.State -eq 'Enabled')
        state = [string]$f.State
      }
    } catch {
      return [ordered]@{
        source = 'Get-WindowsOptionalFeature'
        available = $true
        installed = $false
        state = "QUERY_FAILED_OR_ADMIN_REQUIRED: $($_.Exception.Message)"
      }
    }
  }

  return [ordered]@{
    source = 'none'
    available = $false
    installed = $false
    state = 'FEATURE_QUERY_NOT_AVAILABLE'
  }
}

$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$systemDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'"
$isAdmin = Get-AdminStatus

$dotnet = ''
try {
  $dotnet = (& dotnet --list-runtimes 2>$null | Out-String).Trim()
  if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $dotnet = 'dotnet runtime not detected'
  }
} catch {
  $dotnet = 'dotnet runtime not detected'
}

$iis = Get-FeatureState -ServerFeatureName 'Web-Server' -ClientFeatureName 'IIS-WebServerRole'
$wa  = Get-FeatureState -ServerFeatureName 'Web-Windows-Auth' -ClientFeatureName 'IIS-WindowsAuthentication'

$totalMemoryGB = [math]::Round(($cs.TotalPhysicalMemory / 1GB), 2)
$freeDiskGB = $null
if ($systemDrive -and $systemDrive.FreeSpace) {
  $freeDiskGB = [math]::Round(($systemDrive.FreeSpace / 1GB), 2)
}

$data = [ordered]@{
  evidenceClass = 'LAB_EVIDENCE'
  serverName = $env:COMPUTERNAME
  osVersion = "$($os.Caption) $($os.Version)"
  osArchitecture = [string]$os.OSArchitecture
  isWindowsServer = ($os.Caption -match 'Server')
  isAdministrator = [bool]$isAdmin
  domainJoined = [bool]$cs.PartOfDomain
  domainName = [string]$cs.Domain
  totalPhysicalMemoryGB = $totalMemoryGB
  systemDriveFreeGB = $freeDiskGB
  iis = $iis
  windowsAuthentication = $wa
  dotnetRuntimeVersion = $dotnet
  collectedAt = (Get-Date).ToString('o')
  collectedBy = "$env:USERDOMAIN\$env:USERNAME"
}

$data | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $OutputPath

Write-Host "Written: $OutputPath"
Write-Host "Evidence class: LAB_EVIDENCE"
Write-Host "This collector does not read passwords, connection strings, tokens or private keys."
Write-Host "Review the output before sharing. Do not commit sensitive or personal data."
