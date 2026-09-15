param(
  [ValidateSet('LAB_EVIDENCE','PILOT_ENVIRONMENT_EVIDENCE')]
  [string]$EvidenceClass = 'LAB_EVIDENCE',
  [string]$OutputPath = '.\eims-pilot-environment-evidence.json',
  [string]$ReferenceCustomer = 'Mes Shahid Bahonar',
  [string]$ServerName = '',
  [string]$CollectedByRole = '',
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
  } catch { return $false }
}

function Get-FeatureState([string]$ServerFeatureName,[string]$ClientFeatureName) {
  if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    try {
      $f = Get-WindowsFeature $ServerFeatureName -ErrorAction Stop
      return [ordered]@{ source='Get-WindowsFeature'; queryAvailable=$true; installed=[bool]$f.Installed; state=[string]$f.InstallState }
    } catch {
      return [ordered]@{ source='Get-WindowsFeature'; queryAvailable=$true; installed=$false; state='QUERY_FAILED' }
    }
  }
  if (Get-Command Get-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
    try {
      $f = Get-WindowsOptionalFeature -Online -FeatureName $ClientFeatureName -ErrorAction Stop
      return [ordered]@{ source='Get-WindowsOptionalFeature'; queryAvailable=$true; installed=($f.State -eq 'Enabled'); state=[string]$f.State }
    } catch {
      return [ordered]@{ source='Get-WindowsOptionalFeature'; queryAvailable=$true; installed=$false; state='QUERY_FAILED_OR_ADMIN_REQUIRED' }
    }
  }
  return [ordered]@{ source='none'; queryAvailable=$false; installed=$false; state='FEATURE_QUERY_NOT_AVAILABLE' }
}

function Get-ToolSummary([string]$Name,[string[]]$Arguments=@()) {
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if (-not $cmd) { return [ordered]@{ available=$false; versionSummary=$null } }
  $summary = $null
  try {
    $text = (& $cmd.Source @Arguments 2>&1 | Out-String).Trim()
    if ($text) {
      $summary = ($text -split "`r?`n" | Where-Object { $_ } | Select-Object -First 1).Trim()
      if ($summary.Length -gt 200) { $summary = $summary.Substring(0,200) }
    }
  } catch { $summary = 'VERSION_QUERY_FAILED' }
  return [ordered]@{ available=$true; versionSummary=$summary }
}

function Get-DotnetSummary {
  $activeSdk = $null; $sdks=@(); $runtimes=@()
  if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    try { $activeSdk = (& dotnet --version 2>$null | Out-String).Trim() } catch { }
    try { $sdks = @(& dotnet --list-sdks 2>$null | ForEach-Object { ($_ -split '\s+')[0] } | Where-Object { $_ }) } catch { }
    try { $runtimes = @(& dotnet --list-runtimes 2>$null | ForEach-Object { $p=$_ -split '\s+'; if($p.Length -ge 2){ "$($p[0]) $($p[1])" } } | Where-Object { $_ }) } catch { }
  }
  $runtime = @($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' } | Select-Object -First 1)
  if ($runtime.Count -eq 0) { $runtime = @($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' } | Select-Object -First 1) }
  $runtimeVersion = if ($runtime.Count -gt 0) { (($runtime[0] -split '\s+')[1]) } else { '' }
  return [ordered]@{ available=(-not [string]::IsNullOrWhiteSpace($activeSdk)); activeSdk=$activeSdk; sdks=$sdks; runtimes=$runtimes; runtimeVersion=$runtimeVersion }
}

function Get-CertificateSummary {
  $r=[ordered]@{ queryAvailable=$false; nonExpiredServerAuthCount=0; earliestExpiryUtc=$null }
  try {
    if (-not (Test-Path 'Cert:\LocalMachine\My')) { return $r }
    $r.queryAvailable=$true; $now=Get-Date; $oid='1.3.6.1.5.5.7.3.1'
    $certs=@(Get-ChildItem 'Cert:\LocalMachine\My' -ErrorAction Stop | Where-Object { $_.NotAfter -gt $now -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq $oid }) })
    $r.nonExpiredServerAuthCount=$certs.Count
    if($certs.Count -gt 0){ $r.earliestExpiryUtc=($certs | Sort-Object NotAfter | Select-Object -First 1).NotAfter.ToUniversalTime().ToString('o') }
  } catch { $r.queryAvailable=$false }
  return $r
}

function Get-OracleTcpProbe([string]$HostName,[int]$Port) {
  if ([string]::IsNullOrWhiteSpace($HostName)) { return [ordered]@{ requested=$false; port=$Port; tcpSucceeded=$null } }
  $ok=$false
  try {
    if (Get-Command Test-NetConnection -ErrorAction SilentlyContinue) { $ok=[bool](Test-NetConnection -ComputerName $HostName -Port $Port -WarningAction SilentlyContinue -InformationLevel Quiet) }
  } catch { $ok=$false }
  return [ordered]@{ requested=$true; port=$Port; tcpSucceeded=$ok }
}

$computerSystem=$null; $operatingSystem=$null
try { $computerSystem=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop } catch { }
try { $operatingSystem=Get-CimInstance Win32_OperatingSystem -ErrorAction Stop } catch { }

$osCaption=if($operatingSystem){[string]$operatingSystem.Caption}else{[Environment]::OSVersion.VersionString}
$osVersion=if($operatingSystem){[string]$operatingSystem.Version}else{[Environment]::OSVersion.Version.ToString()}
$osArchitecture=if($operatingSystem){[string]$operatingSystem.OSArchitecture}else{[string]$env:PROCESSOR_ARCHITECTURE}
$isWindowsServer=($osCaption -match 'Server')
$domainJoined=if($computerSystem){[bool]$computerSystem.PartOfDomain}else{$false}
$domainName=''
if($IncludeDomainName -and $computerSystem -and $computerSystem.PartOfDomain){$domainName=[string]$computerSystem.Domain}
$currentName=$null
try{$currentName=[Security.Principal.WindowsIdentity]::GetCurrent().Name}catch{}
$currentPrincipalDomainQualified=(-not [string]::IsNullOrWhiteSpace($currentName) -and $currentName -match '^[^\\]+\\[^\\]+$')
$iis=Get-FeatureState 'Web-Server' 'IIS-WebServerRole'
$winAuth=Get-FeatureState 'Web-Windows-Auth' 'IIS-WindowsAuthentication'
$dotnet=Get-DotnetSummary
$certs=Get-CertificateSummary
$sqlplus=Get-ToolSummary 'sqlplus' @('-V')
$tnsping=Get-ToolSummary 'tnsping'
$tcp=Get-OracleTcpProbe $OracleHost $OraclePort
$collectedAt=(Get-Date).ToUniversalTime().ToString('o')

# Schema 1.1 matches the readiness evaluator. Auto-discovery fills only facts that can be observed safely.
# Unproven environment claims remain blank/false so activation is fail-closed.
$data=[ordered]@{
  schemaVersion='1.1'
  evidenceClass=$EvidenceClass
  referenceCustomer=$ReferenceCustomer
  windows=[ordered]@{
    serverName=$ServerName
    osVersion=("{0} ({1})" -f $osCaption,$osVersion)
    vmProvisioned=$false
    domainJoined=[bool]$domainJoined
    domainName=$domainName
    iisInstalled=[bool]$iis.installed
    windowsAuthenticationInstalled=[bool]$winAuth.installed
    dotnetRuntimeVersion=[string]$dotnet.runtimeVersion
    collectedAt=$collectedAt
    collectedByRole=$CollectedByRole
  }
  oracle=[ordered]@{
    version=if($sqlplus.available){[string]$sqlplus.versionSummary}else{''}
    providerName=''; providerVersion=''; connectionMode=''; serviceAccountName=''; schemaOwner=''
    liveConnectionValidated=$false; evidenceRef=''
  }
  p1Authority=[ordered]@{ physicalPackageAvailable=$false; packageRef=''; buildPassed=$false; contractTestsPassed=$false; evidenceRef='' }
  windowsIdentity=[ordered]@{ identityFormat=if($currentPrincipalDomainQualified){'DOMAIN\\user'}else{''}; liveDomainIdentityValidated=$false; personIdMappingValidated=$false; serverRoleScopeValidated=$false; clientIdentityHeadersTrusted=$false; evidenceRef='' }
  hrOrg=[ordered]@{ sourceSystem='Oracle HR'; exportOwnerRole='HRIT'; realExportPrepared=$false; p4SchemaValidated=$false; reconciled=$false; approvalRef=''; evidenceRef='' }
  tls=[ordered]@{ configured=$false; hostname=''; certificateSubject=''; validTo=''; liveHandshakeValidated=$false; evidenceRef='' }
  runtime=[ordered]@{ concurrencyValidated=$false; idempotencyValidated=$false; auditOutboxAtomicityValidated=$false; evidenceRef='' }
  operations=[ordered]@{ backupMethod=''; backupRestoreValidated=$false; monitoringTarget=''; monitoringValidated=$false; evidenceRef='' }
  security=[ordered]@{ noSecretsCommitted=$true; noPersonalDataCommitted=$true; reviewedByRole=''; reviewDate='' }
  discovery=[ordered]@{
    platform=[ordered]@{ osCaption=$osCaption; osVersion=$osVersion; osArchitecture=$osArchitecture; isWindowsServer=[bool]$isWindowsServer; isAdministrator=[bool](Get-AdminStatus); powershellVersion=[string]$PSVersionTable.PSVersion }
    domain=[ordered]@{ joined=[bool]$domainJoined; nameIncludedByExplicitRequest=[bool]$IncludeDomainName; currentPrincipalDomainQualified=[bool]$currentPrincipalDomainQualified }
    dotnet=$dotnet
    iis=[ordered]@{ webServer=$iis; windowsAuthentication=$winAuth; webAdministrationModuleAvailable=[bool](Get-Module -ListAvailable WebAdministration -ErrorAction SilentlyContinue) }
    tls=$certs
    oracle=[ordered]@{ sqlplus=$sqlplus; tnsping=$tnsping; tcpProbe=$tcp }
  }
}

$data | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $OutputPath
Write-Host "Written: $OutputPath"
Write-Host "Schema: 1.1"
Write-Host "Evidence class: $EvidenceClass"
Write-Host 'Unproven activation fields remain blank/false by design.'
Write-Host 'No password, connection string, token, private key, or personal identity value is collected.'
