# ACR-S1 — Oracle core persistence schema v1.0
Status: PROPOSED (accepted when verify_core.sql is all PASS on the pilot Oracle)

## Scope
Tables for the P2 persistence contract only: aggregate state, audit, domain decisions, outbox, idempotency.
Out of scope (S1b, after reading their records): evaluation plans/assignments/assessment snapshots/G04 assessments and Wave 9–13 stores (portfolio, execution, benefit, knowledge).

## Decisions
| ID | Decision | Reason |
|---|---|---|
| D1 | IDs stay application-generated strings; only audit/outbox get identity `*_SEQ` | domain IDs already exist in code; SEQ gives total order for audit and dispatch |
| D2 | Optimistic concurrency = `UPDATE … WHERE id=:id AND ENTITY_VERSION=:expected`, 0 rows ⇒ conflict | maps to If-Match / `expectedVersion` already in Host |
| D3 | Idempotency PK = (command, aggregate, key); result stored as JSON | server-side replay; fingerprint detects key reuse with different body |
| D4 | Audit & decision append-only by (a) no UPDATE/DELETE grant to EIMS_APP, (b) trigger | defence in depth |
| D5 | Dictionaries/collections stored as JSON in CLOB with `IS JSON` check | no second DB lock-in beyond JSON support; SQL Server later: NVARCHAR(MAX)+ISJSON |
| D6 | FKs to aggregate are DEFERRABLE INITIALLY DEFERRED | staging order inside one transaction must not matter |
| D7 | Two accounts: EIMS_OWNER (DDL) and EIMS_APP (DML only) | least privilege; no secrets in repo |
| D8 | Oracle `''`=NULL: app must normalize empty strings | known Oracle pitfall |

## Acceptance
`verify/verify_core.sql` prints PASS for all checks on the pilot Oracle (12.2+).

## Known limits
Written without a live Oracle available to the author; syntax is untested until the acceptance run above.
