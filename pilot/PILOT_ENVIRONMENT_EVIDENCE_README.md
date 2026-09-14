# EIMS Network Pilot Environment Evidence Pack

## Purpose

Use this collector to gather only the non-secret environment facts required to decide whether a Windows machine can act as an EIMS Lab/Pilot host. Run it first on the current lab PC; run the same collector later on the organization Pilot Windows Server/VM.

## Safety boundary

The collector does **not** read or export:

- passwords;
- connection strings;
- tokens;
- private keys;
- HR/personnel records;
- browser/session data;
- arbitrary environment variables;
- registry dumps.

By default it also omits the computer name, user name and Domain name. Domain name is included only when `-IncludeDomainName` is explicitly supplied.

## Current PC / Lab command

From PowerShell in the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\pilot\scripts\Collect-EimsPilotEnvironmentEvidence.ps1 -EvidenceClass LAB_EVIDENCE -OutputPath .\eims-pilot-environment-evidence.json
```

Then review the JSON before sharing it.

## Real Pilot VM command

On the real Windows Server/VM:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\pilot\scripts\Collect-EimsPilotEnvironmentEvidence.ps1 -EvidenceClass PILOT_ENVIRONMENT_EVIDENCE -OutputPath .\eims-pilot-environment-evidence.json
```

If IT approves disclosing the Domain name for readiness evidence, add `-IncludeDomainName`.

## Optional Oracle TCP reachability probe

Only when IT/DBA provides the approved Oracle host name and port:

```powershell
.\pilot\scripts\Collect-EimsPilotEnvironmentEvidence.ps1 `
  -EvidenceClass PILOT_ENVIRONMENT_EVIDENCE `
  -OracleHost '<approved-oracle-host>' `
  -OraclePort 1521 `
  -OutputPath .\eims-pilot-environment-evidence.json
```

The output records only whether a TCP probe was requested, the port, and whether it succeeded. It deliberately does not write the Oracle host/address into the evidence file and never attempts an Oracle login.

## Interpretation

A successful collector run does not itself mean Network Pilot is ready. The evidence is used to resolve these remaining gates:

- Windows Server/VM availability;
- Domain membership;
- IIS and Windows Authentication capability;
- .NET runtime/SDK availability;
- TLS certificate readiness;
- Oracle client/tooling and optional network reachability.

Live IIS Windows Authentication, Oracle authenticated transaction probing, real HR export reconciliation, TLS handshake, backup/restore and monitoring evidence remain separate approval gates.
