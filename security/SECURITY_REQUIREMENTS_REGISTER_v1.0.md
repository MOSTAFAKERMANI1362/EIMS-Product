# EIMS Security Requirements Register v1.0

**Status:** OP-02 baseline  
**Purpose:** Convert security principles into testable product requirements.

Legend: `OPEN` = required but evidence not yet complete; `PARTIAL` = design/partial implementation exists; `VERIFIED` = repeatable evidence exists for the target implementation/environment.

| ID | Requirement | Priority | Initial status | Minimum evidence |
|---|---|---|---|---|
| SEC-AUTH-001 | Every state-changing command requires authenticated server identity. | P0 | PARTIAL | integration negative/positive tests |
| SEC-AUTH-002 | Authorization must evaluate current Role + Scope server-side. | P0 | PARTIAL | object/action authorization tests |
| SEC-AUTH-003 | Direct API invocation must not bypass UI restrictions. | P0 | PARTIAL | direct-call negative tests |
| SEC-AUTH-004 | Read access must enforce object/unit scope to prevent IDOR. | P0 | OPEN | cross-user/cross-unit tests |
| SEC-AUTH-005 | Assignment-bound work must validate exact Assignment/Entity/Role context. | P0 | OPEN | stale/wrong assignment tests |
| SEC-AUTH-006 | Expired/deactivated identity or role assignment fails closed. | P0 | OPEN | identity validity tests |
| SEC-SOD-001 | Need Owner cannot self-approve independent G03 review. | P0 | PARTIAL | SoD tests |
| SEC-SOD-002 | Committee vote authority is distinct from final G04 decision authority. | P0 | PARTIAL | role/command tests |
| SEC-SOD-003 | Execution Owner cannot independently approve own completion where independence is required. | P0 | PARTIAL | SoD test |
| SEC-SOD-004 | Benefit verification remains independent from benefit ownership according to policy. | P0 | PARTIAL | SoD test |
| SEC-SOD-005 | Knowledge validation and publication authorities remain distinct. | P0 | PARTIAL | SoD test |
| SEC-STATE-001 | State transitions are accepted only from allowed current states. | P0 | PARTIAL | state-machine tests |
| SEC-STATE-002 | `expectedVersion` enforces optimistic concurrency. | P0 | PARTIAL | race/conflict tests |
| SEC-STATE-003 | Idempotency prevents duplicate side effects on replay/retry. | P0 | PARTIAL | replay tests |
| SEC-STATE-004 | Historical decisions/snapshots are not silently overwritten. | P0 | PARTIAL | history/version tests |
| SEC-STATE-005 | G04 votes are unique per authorized frozen committee member. | P0 | PARTIAL | duplicate vote/membership tests |
| SEC-AUD-001 | Every privileged/state-changing action records actor PersonID, role, assignment/scope where applicable, entity version, timestamp and correlation. | P0 | PARTIAL | audit contract tests |
| SEC-AUD-002 | State + Audit + Outbox commit atomically. | P0 | OPEN | database fault-injection/integration evidence |
| SEC-AUD-003 | Audit records are append-only to ordinary application roles. | P0 | OPEN | DB privilege/negative tests |
| SEC-AUD-004 | Legacy unknown actor data must not be fabricated during migration. | P1 | PARTIAL | migration rule/test |
| SEC-DATA-001 | Sensitive data is classified and handled according to DATA_CLASSIFICATION_v1.0. | P0 | PARTIAL | design + configuration evidence |
| SEC-DATA-002 | Production/customer secrets never reside in source repository or client HTML. | P0 | PARTIAL | secret scan + review |
| SEC-DATA-003 | Logs and diagnostics redact secrets and unnecessary personal data. | P0 | OPEN | log tests/support-bundle review |
| SEC-DATA-004 | Retention/archive does not silently delete records required for audit/history. | P1 | PARTIAL | archive/retention tests |
| SEC-DB-001 | Database access uses parameterized commands/provider-safe APIs. | P0 | OPEN | code review/SAST/injection tests |
| SEC-DB-002 | Application/service database identities follow least privilege. | P0 | OPEN | DBA privilege evidence |
| SEC-DB-003 | Schema migration is versioned, reviewable and rollback/recovery-aware. | P1 | OPEN | migration tests/runbook |
| SEC-FILE-001 | Uploads enforce allowlisted type/size rules and safe generated storage identifiers. | P0 | OPEN | upload abuse tests |
| SEC-FILE-002 | Uploaded files cannot execute from application storage/web root. | P0 | OPEN | deployment/config evidence |
| SEC-FILE-003 | Download authorization is re-evaluated server-side. | P0 | OPEN | cross-scope download tests |
| SEC-NET-001 | Production/pilot HTTP traffic uses approved TLS configuration. | P0 | OPEN | environment scan/evidence |
| SEC-NET-002 | Integration endpoints validate server identity/certificate and fail closed. | P0 | OPEN | integration tests |
| SEC-WEB-001 | Untrusted output is encoded to prevent XSS/DOM injection. | P0 | OPEN | DAST/manual tests |
| SEC-WEB-002 | CSRF/CORS/security-header controls are implemented according to final authentication architecture. | P1 | OPEN | DAST/config evidence |
| SEC-AVAIL-001 | Request/file/search/AI operations have appropriate size, timeout and rate controls. | P1 | OPEN | load/abuse tests |
| SEC-AVAIL-002 | Health/readiness distinguishes process health from dependency readiness. | P1 | PARTIAL | deployment smoke tests |
| SEC-SUPPLY-001 | Dependencies are continuously scanned for known vulnerabilities. | P0 | OPEN | SCA pipeline |
| SEC-SUPPLY-002 | Every release produces an SBOM. | P1 | OPEN | CI artifact |
| SEC-SUPPLY-003 | Release artifact integrity is hash-verified and later signed for commercial release. | P1 | PARTIAL | release pipeline evidence |
| SEC-SUPPLY-004 | Secret scanning runs before merge/release. | P0 | OPEN | CI gate |
| SEC-AI-001 | AI is advisory; it cannot perform Final Decision or sovereign Business Commit. | P0 | PARTIAL | gateway/contract negative tests |
| SEC-AI-002 | AI retrieval is constrained by current user PersonID/Role/Scope. | P0 | OPEN | RAG authorization tests |
| SEC-AI-003 | Prompt injection and external data-egress policy are enforced. | P0 | PARTIAL | adversarial tests |
| SEC-AI-004 | Sensitive data is minimized before model invocation and provider use follows customer policy. | P0 | OPEN | policy + integration tests |
| SEC-AI-005 | Model/provider outage must not block core EIMS workflow. | P1 | PARTIAL | failure-mode test |
| SEC-AI-006 | AI-generated CandidatePatch/draft requires human confirmation before application. | P0 | PARTIAL | workflow tests |
| SEC-OPS-001 | Backup and restore are tested, not assumed. | P0 | OPEN | successful restore drill |
| SEC-OPS-002 | Security events/privileged changes are monitorable and alertable. | P1 | OPEN | monitoring evidence |
| SEC-OPS-003 | Vulnerability intake, triage, remediation SLA and retest process exist before commercial release. | P1 | OPEN | process/runbook |
| SEC-OPS-004 | Critical security findings block commercial release unless fixed; no silent waiver. | P0 | OPEN | release policy/gate evidence |

## Status rule

No item may move to `VERIFIED` solely because code exists. Verification requires repeatable evidence against the implementation and, when environment-dependent, the actual target environment.

## Immediate automation backlog

1. Secret scanning gate.
2. Dependency/SCA scanning.
3. SBOM generation.
4. SAST baseline.
5. Build + unit/contract/security negative tests.
6. DAST/API scan in staging.
7. Authorization/IDOR regression suite.
8. Release artifact hash/signing process.
