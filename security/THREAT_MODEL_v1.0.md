# EIMS Threat Model v1.0

**Status:** Baseline for OP-02  
**Scope:** Commercial product + Mes Shahid Bahonar Network Pilot  
**Product reference:** Frozen v6.360 Executable Product Specification and server-authoritative target architecture.

## 1. Security objectives

EIMS must preserve confidentiality, integrity, availability, accountability and separation of duties across the full Digital Thread:

`ImprovementSource → Case → Need → Idea → Portfolio → Execution → Benefit → Knowledge`

The primary security objective is not only to stop data theft; it is also to prevent unauthorized business decisions, silent workflow mutation, role bypass and loss of audit evidence.

## 2. Primary assets

1. Canonical business records and historical versions.
2. G01–G04 decisions and committee votes.
3. Role assignments, scopes and segregation-of-duties constraints.
4. Audit trail and event outbox.
5. HR/person/organization mappings.
6. Portfolio, execution and benefit evidence.
7. Attachments and knowledge assets.
8. Configuration, rule sets and thresholds.
9. Authentication/authorization context.
10. Database, service and integration credentials.
11. AI prompts, retrieved enterprise context and generated CandidatePatch/drafts.
12. Release artifacts, migrations and deployment configuration.

## 3. Trust boundaries

- User browser ↔ Web/API application.
- Web/API ↔ Domain/Authority layer.
- Domain layer ↔ Persistence/database.
- Application ↔ Windows/enterprise identity provider.
- Application ↔ HR/organization import or integration adapter.
- Application ↔ external ERP/integration endpoints.
- Application ↔ file/object storage.
- Application ↔ AI Gateway ↔ model provider.
- CI/CD ↔ source repository ↔ release artifacts.
- Admin/Support tooling ↔ production environment.

## 4. Threat actors

- Unauthenticated external attacker.
- Authenticated user exceeding assigned role/scope.
- Insider with legitimate access attempting privilege abuse.
- Administrator or operator making accidental/destructive changes.
- Compromised service account or integration endpoint.
- Malicious uploaded file or content.
- Supply-chain/dependency attacker.
- AI prompt-injection/data-exfiltration attacker.
- Customer-specific misconfiguration.

## 5. Critical abuse cases

### TM-01 — Direct API authorization bypass
A user bypasses UI role guards and calls a state-changing command directly.

**Required controls:** server-side Role+Scope+State+Version+Rule enforcement; negative authorization tests; default deny.

### TM-02 — Cross-scope/IDOR access
A valid user changes an entity/assignment identifier and accesses or mutates another unit's record.

**Required controls:** object-level authorization on every read/write; assignment binding; scope filtering.

### TM-03 — Stale assignment or link reuse
A user opens an old Structured Assessment or Work Item after assignment/state has changed.

**Required controls:** exact Assignment/Idea/Role validation; optimistic concurrency; stale-link rejection.

### TM-04 — Self-approval / SoD bypass
A user performs a decision that must be independent from their earlier role.

**Required controls:** explicit SoD matrix; server-side actor-history checks; immutable decision snapshots.

### TM-05 — Committee/final-decision manipulation
Votes are overwritten, duplicated, cast outside frozen membership, or final authority is impersonated.

**Required controls:** unique member vote; frozen committee snapshot; immutable vote history; distinct final-decision authority.

### TM-06 — Concurrent update/lost update
Two users update the same aggregate and one silently overwrites the other.

**Required controls:** expectedVersion / optimistic concurrency; conflict response; retry rules.

### TM-07 — Duplicate command/replay
Network retry or malicious replay repeats a state-changing side effect.

**Required controls:** idempotency key; replay-safe result; command correlation.

### TM-08 — Audit tampering
An actor changes/deletes audit history or performs a state change without equivalent audit evidence.

**Required controls:** append-only audit; State+Audit+Outbox atomic transaction; restricted DB privileges; audit integrity monitoring.

### TM-09 — Injection/data-layer compromise
Input reaches SQL, template, log or shell context unsafely.

**Required controls:** parameterized access, validation/encoding, no shell construction from user input, secure logging.

### TM-10 — Malicious attachment
Uploaded file delivers malware, active content, oversized payload or unauthorized information.

**Required controls:** type/size allowlist, generated storage name, content inspection where available, download authorization, no execution from upload storage.

### TM-11 — Secret disclosure
Credentials appear in repository, logs, configuration files, support bundles or AI prompts.

**Required controls:** secrets manager/environment binding, secret scanning, log redaction, repository policy, incident rotation procedure.

### TM-12 — Identity mapping confusion
Wrong Windows identity maps to wrong PersonID/Role or stale employee record remains authorized.

**Required controls:** canonical mapping, validity period, unique binding, fail-closed behavior, reconciliation and audit.

### TM-13 — Configuration abuse
Customer/admin modifies a value that should be a frozen product invariant.

**Required controls:** configuration boundary schema; privileged configuration role; versioning; validation; change audit; ACR boundary.

### TM-14 — Backup/restore failure
Data is backed up but cannot be restored consistently with files/audit/outbox.

**Required controls:** documented RPO/RTO; restore drills; integrity verification; migration-aware recovery.

### TM-15 — Supply-chain compromise
Malicious/vulnerable dependency or build artifact enters release.

**Required controls:** locked dependencies, SCA, SBOM, trusted build, signed/hash-verified release, dependency review.

### TM-16 — AI prompt injection / data exfiltration
Untrusted text persuades model/gateway to reveal restricted enterprise information or bypass controls.

**Required controls:** role-scoped retrieval, prompt injection controls, data minimization, egress policy, provenance/citations, no autonomous business authority.

### TM-17 — AI excessive agency
AI performs approve/reject/final decision/write operations without human authority.

**Required controls:** CandidatePatch/draft only; explicit user confirmation; business commit disabled in AI boundary; auditable human actor.

### TM-18 — Availability/resource exhaustion
Large requests, repeated searches, AI calls or files exhaust CPU/memory/storage/database connections.

**Required controls:** rate/size limits, timeouts, connection pooling, quotas, health monitoring and graceful degradation.

## 6. Security invariants

- UI visibility is never the security boundary.
- No state-changing command succeeds without authenticated actor and current authorization context.
- No business transition silently overwrites history.
- No AI output becomes a sovereign business decision.
- No production secret is stored in source control.
- Audit records are not rewritten to make legacy/unknown actor data look known.
- Every externally supplied identifier is treated as untrusted until authorized against current actor scope.

## 7. Verification strategy

Threats above must map to repeatable security requirements and tests. Before Commercial Release Candidate, all Critical/High threat scenarios require implementation evidence and independent penetration-test coverage where applicable.
