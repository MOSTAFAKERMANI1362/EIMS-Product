param(
  [ValidateSet('LAB_EVIDENCE','PILOT_ENVIRONMENT_EVIDENCE')]
  [string]$EvidenceClass = 'LAB_EVIDENCE',

  [string]$OutputPath = '.\eims-pilot-environment-evidence.json',

  [switch]$IncludeDomainName,

  [string]$OracleHost,

  [ValidateRange(1,65535)]
  [int]$OraclePort = 1521
)

$ErrorActionPreference = 'Stop'

function Get-AdminStatus {
  try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return [bool]$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
      $feature = Get-WindowsFeature $ServerFeatureName -ErrorAction Stop
      return [ordered]@{
        source = 'Get-WindowsFeature'
        queryAvailable = $true
        installed = [bool]$feature.Installed
        state = [string]$feature.InstallState
      }
    } catch {
      return [ordered]@{
        source = 'Get-WindowsFeature'
        queryAvailable = $true
        installed = $false
        state = 'QUERY_FAILED'
      }
    }
  }

  if (Get-Command Get-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
    try {
      $feature = Get-WindowsOptionalFeature -Online -FeatureName $ClientFeatureName -ErrorAction Stop
      return [ordered]@{
        source = 'Get-WindowsOptionalFeature'
        queryAvailable = $true
        installed = ($feature.State -eq 'Enabled')
        state = [string]$feature.State
      }
    } catch {
      return [ordered]@{
        source = 'Get-WindowsOptionalFeature'
        queryAvailable = $true
        installed = $false
        state = 'QUERY_FAILED_OR_ADMIN_REQUIRED'
      }
    }
  }

  return [ordered]@{
    source = 'none'
    queryAvailable = $false
    installed = $false
    state = 'FEATURE_QUERY_NOT_AVAILABLE'
  }
}

function Get-ToolSummary {
  param(
    [string]$Name,
    [string[]]$Arguments = @()
  )

  $command = Get-Command $Name -ErrorAction SilentlyContinue
  if (-not $command) {
    return [ordered]@{
      available = $false
      versionSummary = $null
    }
  }

  $summary = $null
  try {
    $text = (& $command.Source @Arguments 2>&1 | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($text)) {
      $summary = ($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1).Trim()
      if ($summary.Length -gt 200) { $summary = $summary.Substring(0,200) }
    }
  } catch {
    $summary = 'VERSION_QUERY_FAILED'
  }

  return [ordered]@{
    available = $true
    versionSummary = $summary
  }
}

function Get-DotnetSummary {
  $version = $null
  $sdks = @()
  $runtimes = @()

  if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    try { $version = (& dotnet --version 2>$null | Out-String).Trim() } catch { }
    try {
      $sdks = @(& dotnet --list-sdks 2>$null | ForEach-Object {
        $parts = $_ -split '\s+'
        if ($parts.Length -gt 0) { $parts[0] }
      } | Where-Object { $_ })
    } catch { }
    try {
      $runtimes = @(& dotnet --list-runtimes 2>$null | ForEach-Object {
        $parts = $_ -split '\s+'
        if ($parts.Length -ge 2) { "$($parts[0]) $($parts[1])" }
      } | Where-Object { $_ })
    } catch { }
  }

  return [ordered]@{
    available = -not [string]::IsNullOrWhiteSpace($version)
    activeSdk = $version
    sdks = $sdks
    runtimes = $runtimes
  }
}

function Get-CertificateSummary {
  $result = [ordered]@{
    queryAvailable = $false
    nonExpiredServerAuthCount = 0
    earliestExpiryUtc = $null
  }

  try {
    if (-not (Test-Path 'Cert:\LocalMachine\My')) { return $result }
    $result.queryAvailable = $true
    $now = Get-Date
    $serverAuthOid = '1.3.6.1.5.5.7.3.1'
    $certs = @(Get-ChildItem 'Cert:\LocalMachine\My' -ErrorAction Stop | Where-Object {
      $_.NotAfter -gt $now -and
      ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq $serverAuthOid })
    })
    $result.nonExpiredServerAuthCount = $certs.Count
    if ($certs.Count -gt 0) {
      $earliest = $certs | Sort-Object NotAfter | Select-Object -First 1
      $result.earliestExpiryUtc = $earliest.NotAfter.ToUniversalTime().ToString('o')
    }
  } catch {
    $result.queryAvailable = $false
  }

  return $result
}

function Get-OracleTcpProbe {
  param([string]$HostName,[int]$Port)

  if ([string]::IsNullOrWhiteSpace($HostName)) {
    return [ordered]@{
      requested = $false
      port = $Port
      tcpSucceeded = $null
    }
  }

  $succeeded = $false
  try {
    if (Get-Command Test-NetConnection -ErrorAction SilentlyContinue) {
      $probe = Test-NetConnection -ComputerName $HostName -Port $Port -WarningAction SilentlyContinue -InformationLevel Quiet
      $succeeded = [bool]$probe
    } else {
      $client = New-Object System.Net.Sockets.TcpClient
      try {
        $async = $client.BeginConnect($HostName,$Port,$null,$null)
        if ($async.AsyncWaitHandle.WaitOne(3000,$false)) {
          $client.EndConnect($async)
          $succeeded = $true
        }
      } finally {
        $client.Close()
      }
    }
  } catch {
    $succeeded = $false
  }

  return [ordered]@{
    requested = $true
    port = $Port
    tcpSucceeded = $succeeded
  }
}

$computerSystem = $null
$operatingSystem = $null
try { $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop } catch { }
try { $operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch { }

$osCaption = if ($operatingSystem) { [string]$operatingSystem.Caption } else { [Environment]::OSVersion.VersionString }
$osVersion = if ($operatingSystem) { [string]$operatingSystem.Version } else { [Environment]::OSVersion.Version.ToString() }
$osArchitecture = if ($operatingSystem) { [string]$operatingSystem.OSArchitecture } else { [string]$env:PROCESSOR_ARCHITECTURE }
$isWindowsServer = ($osCaption -match 'Server')
$domainJoined = if ($computerSystem) { [bool]$computerSystem.PartOfDomain } else { $false }
$domainName = $null
if ($IncludeDomainName -and $computerSystem -and $computerSystem.PartOfDomain) { $domainName = [string]$computerSystem.Domain }

$currentName = $null
try { $currentName = [Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { }
$currentPrincipalDomainQualified = -not [string]::IsNullOrWhiteSpace($currentName) -and ($currentName -match '^[^\\]+\\[^\\]+$')

$iis = Get-FeatureState -ServerFeatureName 'Web-Server' -ClientFeatureName 'IIS-WebServerRole'
$windowsAuth = Get-FeatureState -ServerFeatureName 'Web-Windows-Auth' -ClientFeatureName 'IIS-WindowsAuthentication'

$data = [ordered]@{
  evidenceSchema = 'EIMS-PILOT-ENV-EVIDENCE-1.0'
  evidenceClass = $EvidenceClass
  collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
  platform = [ordered]@{
    computerName = [string]$env:COMPUTERNAME
    osCaption = $osCaption
    osVersion = $osVersion
    osArchitecture = $osArchitecture
    isWindowsServer = [bool]$isWindowsServer
    isAdministrator = [bool](Get-AdminStatus)
    powershellVersion = [string]$PSVersionTable.PSVersion
  }
  domain = [ordered]@{
    joined = [bool]$domainJoined
    nameIncludedByExplicitRequest = [bool]$IncludeDomainName
    name = $domainName
    currentPrincipalDomainQualified = [bool]$currentPrincipalDomainQualified
  }
  dotnet = Get-DotnetSummary
  iis = [ordered]@{
    webServer = $iis
    windowsAuthentication = $windowsAuth
    webAdministrationModuleAvailable = [bool](Get-Module -ListAvailable WebAdministration -ErrorAction SilentlyContinue)
  }
  tls = Get-CertificateSummary
  oracle = [ordered]@{
    sqlplus = Get-ToolSummary -Name 'sqlplus' -Arguments @('-V')
    tnsping = Get-ToolSummary -Name 'tnsping'
    tcpProbe = Get-OracleTcpProbe -HostName $OracleHost -Port $OraclePort
  }
}

$data | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $OutputPath

Write-Host "Written: $OutputPath"
Write-Host "Evidence class: $EvidenceClass"
Write-Host 'No password, connection string, token or private key is collected.'
Write-Host 'Review the JSON before sharing or committing it.'
