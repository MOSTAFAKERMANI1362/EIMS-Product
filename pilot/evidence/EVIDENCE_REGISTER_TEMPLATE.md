# EIMS Network Pilot — Evidence Register Template

| ID | Area | Evidence | Owner Role | Status | Evidence Ref | Notes |
|---|---|---|---|---|---|---|
| EV-WIN-001 | Windows | Pilot server/VM identified | IT | OPEN |  |  |
| EV-WIN-002 | Windows | Domain membership verified | IT | OPEN |  |  |
| EV-IIS-001 | IIS | IIS installed | IT | OPEN |  |  |
| EV-IIS-002 | IIS | Windows Authentication usable | IT | OPEN |  |  |
| EV-ID-001 | Identity | Live domain identity reaches server | IT/Dev | OPEN |  |  |
| EV-ID-002 | Identity | Windows Identity → PersonID mapping | IT/HRIT/Dev | OPEN |  |  |
| EV-ID-003 | Identity | Role/Scope server authorization | Dev/Security | OPEN |  |  |
| EV-ORA-001 | Oracle | Oracle version/provider identified | DBA | OPEN |  |  |
| EV-ORA-002 | Oracle | Least-privilege live connection validated | DBA/Dev | OPEN |  |  |
| EV-ORA-003 | Oracle | Schema/service identity names approved | DBA | OPEN |  |  |
| EV-HR-001 | HR | Real export mapped to P4 schema | HRIT | OPEN |  |  |
| EV-HR-002 | HR | Reconciliation/approval completed | HRIT | OPEN |  |  |
| EV-TLS-001 | TLS | Certificate/hostname configured | IT/Security | OPEN |  |  |
| EV-TLS-002 | TLS | Live TLS handshake validated | IT/Security | OPEN |  |  |
| EV-RUN-001 | Runtime | Optimistic concurrency validated | Dev/DBA | OPEN |  |  |
| EV-RUN-002 | Runtime | Idempotency replay validated | Dev/DBA | OPEN |  |  |
| EV-RUN-003 | Runtime | State + Audit + Outbox atomicity validated | Dev/DBA | OPEN |  |  |
| EV-BACK-001 | Operations | Backup method approved | IT/DBA | OPEN |  |  |
| EV-BACK-002 | Operations | Restore drill passed | IT/DBA | OPEN |  |  |
| EV-MON-001 | Operations | Monitoring/logging validated | IT/Dev | OPEN |  |  |

## Status values

- `OPEN`: evidence not yet produced
- `IN_PROGRESS`: test/collection underway
- `PASS`: repeatable evidence exists
- `FAIL`: test performed and failed
- `BLOCKED`: external dependency prevents test
- `N/A`: only after explicit technical approval

## Evidence rules

1. `PASS` requires a repeatable test/log/approved record; prose alone is insufficient.
2. Do not paste secrets, personal data, production logs or full connection strings into this register.
3. Evidence references should point to approved internal storage or sanitized artifacts.
4. A failed test is valuable evidence; do not rewrite it as PASS.
