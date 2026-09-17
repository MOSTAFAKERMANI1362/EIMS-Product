# EIMS Wave16 — Physical Pilot Readiness Runbook

Status: PRE-PILOT / FAIL-CLOSED
Reference customer: Mes Shahid Bahonar
Prerequisite: Wave15 merged to `main` as `e9ca97688ef481209762253a8ec392738ee7fb7b`.

## Objective

Prepare the real Mes Shahid Bahonar environment so the merged Wave15 activation gate can safely select a production runtime composition. This runbook does **not** authorize activation by configuration alone and does not allow LAB evidence to substitute for real PILOT evidence.

## Non-negotiable rules

1. Do not commit passwords, connection strings, private keys, certificates, HR exports, national IDs, salaries, bank information or other personal data to Git.
2. `PILOT_ENVIRONMENT_EVIDENCE` must contain references and validation assertions only; raw sensitive material remains in approved operational storage.
3. `LAB_EVIDENCE` can validate tooling but can never activate the Network Pilot.
4. The Host remains `FailClosedCommandGateway` until all OP-04 gates pass **and** the real `IProductionRuntimeComposition` reports durable P2, authoritative P3 and a non-fail-closed candidate gateway.
5. Frozen product specification `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` is not modified by this activity.

## Required sequence

### 1. Windows Server / VM

Owner: IT Infrastructure

Required evidence:
- server/VM provisioned
- Windows Server version recorded
- machine joined to corporate domain
- IIS installed
- Windows Authentication feature installed
- .NET 10 runtime available

OP-04 gate: `WINDOWS_HOST`

Do not mark PASS on a Windows 11 workstation or non-domain LAB machine.

### 2. Oracle physical P2

Owner: DBA + Application IT

Required facts, without credentials:
- Oracle server/database version
- approved .NET Oracle provider name and version
- connectivity mode
- service account name/model
- EIMS schema owner
- live connection validation completed from the Pilot Host
- evidence reference issued by DBA/IT

OP-04 gate: `ORACLE_P2`

Important: the repository may record the provider/version and evidence reference, but never the database password or connection string.

### 3. P1 authority package

Owner: EIMS Technical Lead / Application IT

Required evidence:
- physical package available on Pilot Host
- build PASS
- contract tests PASS
- package/evidence reference

OP-04 gate: `P1_AUTHORITY_PACKAGE`

### 4. Windows identity / P3

Owner: Domain/IAM + Application IT

Required validation:
- real authenticated identity format is `DOMAIN\\user`
- Windows principal resolves to exactly one active PersonID
- exact Assignment resolves server-side
- role and scope are resolved server-side
- client identity/role/scope headers are not trusted

OP-04 gate: `WINDOWS_IDENTITY_P3`

Do not infer EIMS roles from HR job titles.

### 5. HR / organization P4

Owner: HR/HRIT

Required validation:
- real Oracle HR export prepared through approved channel
- canonical P4 schema validation PASS
- reconciliation PASS
- HR approval reference recorded
- evidence reference recorded

OP-04 gate: `HR_ORG_P4`

Never commit the real HR export to Git.

### 6. TLS

Owner: IT Infrastructure / Security

Required validation:
- approved hostname
- certificate installed
- subject and validity recorded
- live HTTPS/TLS handshake validated from an approved client

OP-04 gate: `TLS`

Do not commit private keys, PFX, PEM or certificate secrets.

### 7. Runtime proof

Owner: EIMS Technical Lead

Required physical-environment proof:
- optimistic concurrency behavior validated
- idempotency validated
- state + audit + outbox atomicity validated
- evidence reference created

OP-04 gate: `RUNTIME_PROOF`

Wave15 LAB regression is supporting evidence only; physical-environment proof is still required.

### 8. Operations

Owner: IT Operations / DBA

Required validation:
- backup method defined
- backup/restore test completed
- monitoring target defined
- monitoring validated
- evidence reference recorded

OP-04 gate: `OPERATIONS`

### 9. Security evidence

Owner: Security / IT

Required assertions:
- no secrets committed
- no personal data committed
- security review role recorded
- review date recorded

OP-04 gate: `SECURITY_EVIDENCE`

## Activation decision

Activation is allowed only when both layers are true:

1. `EnvironmentEvidenceEvaluator` returns `PilotActivationReady = true` for evidence class `PILOT_ENVIRONMENT_EVIDENCE` and all nine OP-04 gates pass with no sensitive keys.
2. `IProductionRuntimeComposition` reports:
   - `DurableP2Bound = true`
   - `AuthoritativeP3Bound = true`
   - `CandidateGateway != null`
   - candidate is not `FailClosedCommandGateway`

Any missing condition keeps the Host fail-closed.

## Required evidence handling

Each `evidenceRef` should point to an approved internal artifact, ticket, change record, test report or controlled operational document. Evidence references are identifiers only; confidential material stays outside Git.

## Exit criteria for Wave16 software-side work

Wave16 software-side work is complete when:
- sanitized OP-04 template exists
- responsibility matrix exists
- preflight/validation tooling exists and fails closed
- production-composition binding plan is frozen
- every remaining item requiring the corporate environment is explicitly listed as a blocker rather than simulated

Network Pilot readiness itself is **not** complete until real environment evidence passes OP-04 and the physical composition is bound and validated.
