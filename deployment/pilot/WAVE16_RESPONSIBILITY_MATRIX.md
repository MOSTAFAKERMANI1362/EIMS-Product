# EIMS Wave16 — Pilot Responsibility Matrix

| OP-04 Gate | Primary owner | Supporting roles | What must be provided | What must NOT enter Git |
|---|---|---|---|---|
| WINDOWS_HOST | IT Infrastructure | EIMS Technical Lead | Server/VM facts, domain join, IIS, Windows Authentication, .NET runtime, evidence ref | local admin passwords, service credentials |
| ORACLE_P2 | DBA / Application IT | EIMS Technical Lead | Oracle/provider versions, connectivity mode, service account name/model, schema owner, live connectivity proof, evidence ref | DB password, connection string, wallet/private material |
| P1_AUTHORITY_PACKAGE | EIMS Technical Lead | Application IT | deployed package ref, build/test evidence | private environment secrets |
| WINDOWS_IDENTITY_P3 | Domain/IAM | Application IT, EIMS Technical Lead | live DOMAIN\\user proof, PersonID mapping, exact assignment/role/scope proof | user password, auth tokens, identity secrets |
| HR_ORG_P4 | HR/HRIT | EIMS Technical Lead | approved real export through controlled channel, schema/reconciliation/approval evidence refs | HR export, national IDs, salary/bank/personal data |
| TLS | IT Infrastructure / Security | Application IT | hostname, cert subject/expiry, live handshake proof, evidence ref | PFX/PEM/private key/password |
| RUNTIME_PROOF | EIMS Technical Lead | DBA/Application IT | concurrency, idempotency, atomicity physical-environment test refs | sensitive payloads or credentials |
| OPERATIONS | IT Operations / DBA | Security | backup method, restore test, monitoring validation, evidence ref | backup contents, credentials |
| SECURITY_EVIDENCE | Security | EIMS Technical Lead, IT | review role/date, no-secrets/no-PII assertions | findings containing secrets or personal data |

## Decision authority

No individual role may override the Wave15 activation gate. A human approval or configuration flag cannot substitute for failed/missing OP-04 evidence or missing physical P2/P3 composition.

## Handoff order

1. IT Infrastructure provisions Windows Server/VM and IIS/Windows Authentication.
2. DBA/Application IT establishes approved Oracle connectivity and schema/service-account model.
3. Domain/IAM validates Windows identity and authoritative P3 resolution.
4. HR/HRIT supplies the controlled HR export for P4 validation/reconciliation.
5. IT/Security complete TLS, backup/restore, monitoring and security evidence.
6. EIMS Technical Lead executes physical runtime proof and assembles the sanitized OP-04 evidence JSON.
7. OP-04 evaluator must pass all nine gates.
8. Production runtime composition is bound and validated.
9. Only then may Host activation be considered for OP-05 End-to-End Pilot.
