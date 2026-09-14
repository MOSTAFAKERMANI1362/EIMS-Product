# EIMS OWASP ASVS 5.0 Applicability Matrix v1.0

**Status:** OP-02 security baseline  
**Purpose:** Define which ASVS control families apply to EIMS and how they enter the product release process.

> This matrix is a product applicability baseline, not a claim of ASVS certification or complete verification. Individual ASVS requirements will be traced to implementation/test evidence as production code matures.

## Applicability

| ASVS security area | Applicability | EIMS interpretation / required evidence |
|---|---|---|
| Architecture, Design and Threat Modeling | REQUIRED | Threat model, trust boundaries, data flows, security invariants, architecture review |
| Authentication | REQUIRED | Windows/enterprise identity for pilot; future adapter model; fail-closed authentication |
| Session Management | REQUIRED | Secure authenticated sessions/tokens, expiry, logout, anti-replay appropriate to deployment |
| Access Control | CRITICAL | Role+Scope+Assignment+State server authorization, object-level authorization, SoD negative tests |
| Input Validation / Encoding | REQUIRED | Server validation, parameterized persistence, output encoding, canonicalization |
| Stored Cryptography | REQUIRED | TLS, protected secrets, approved key/certificate handling, encryption policy for sensitive stores |
| Error Handling and Logging | CRITICAL | No sensitive leakage; structured security/audit logs; correlation; tamper resistance |
| Data Protection | CRITICAL | Classification, minimization, retention, export controls, privacy handling |
| Communication Security | REQUIRED | TLS and secure integration channels; certificate validation; no plaintext secrets |
| Malicious Code / Supply Chain | REQUIRED | SCA, SBOM, dependency governance, trusted release artifacts |
| Business Logic | CRITICAL | G01-G04 invariants, state machine, concurrency, idempotency, duplicate vote prevention, SoD |
| Files and Resources | REQUIRED | Safe upload/download, type/size validation, storage isolation, authorization |
| API and Web Service | CRITICAL | Authentication, authorization, schema validation, replay protection, rate/size controls |
| Configuration | REQUIRED | Secure defaults, environment separation, invariant/config boundaries, secrets externalized |
| HTTP Security | REQUIRED | Security headers/cookie settings/CORS/CSRF controls as appropriate to final UI/API architecture |
| Serialization / Deserialization | REQUIRED | Strict schemas, safe parsers, no unsafe polymorphic deserialization |
| Web Frontend Security | REQUIRED | XSS/DOM injection protection, no security reliance on hidden UI, content restrictions |
| WebSocket / Realtime (if introduced) | CONDITIONAL | Must be reviewed before use; not assumed by current baseline |
| OAuth/OIDC (future adapters) | CONDITIONAL | Applies if Entra/OIDC/cloud identity adapter is introduced |
| Multi-tenant isolation | FUTURE/CONDITIONAL | Commercial v1 targets on-prem single-tenant; tenant-awareness must not create false isolation claims |

## EIMS priority security themes

### P0 — Release blockers
1. Broken access control / IDOR / scope bypass.
2. SoD bypass or self-approval.
3. Direct command execution without server authority.
4. Audit omission/tampering.
5. State corruption through concurrent/replayed commands.
6. Credential/secret exposure.
7. SQL/injection paths.
8. Malicious file execution.
9. Critical dependency/supply-chain compromise.
10. AI autonomous business authority or unrestricted data exfiltration.

### P1 — Must close before commercial RC
- Session hardening.
- Security headers/CORS/CSRF as applicable.
- Rate/size limits.
- Detailed privacy/retention enforcement.
- Backup/restore security.
- Administrative configuration hardening.
- Monitoring/alerting for privileged/security events.

## Verification evidence types

A control may be marked VERIFIED only with one or more of:
- automated unit/integration/security test,
- architecture fitness test,
- repeatable scanner result reviewed by engineer,
- manual security test with recorded evidence,
- independent penetration-test finding/retest,
- configuration/deployment evidence from target environment.

Prose, an AI answer, or a checkbox without evidence is not verification.

## Release use

The detailed per-requirement ASVS traceability register will be incrementally populated during Production Backend implementation. Any ASVS requirement marked applicable but without evidence remains OPEN; Critical access-control/business-logic items block Commercial Release Candidate.
