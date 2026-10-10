# ACR-S2a — Oracle adapter for the kernel commit path v1.0
Status: PROPOSED (accepted when the conformance suite prints ALL PASS for InMemory AND Oracle)

## Scope
`OracleAuthorityStore : IAuthorityStore` — aggregate read, idempotency read, and ONE-transaction commit of state + decisions + audit + outbox + idempotency (tables V001).
Out of scope: evaluation plan/assignments (S2b), assessment snapshots + G04 (S2c), portfolio/execution/benefit/knowledge stores (S2d), outbox dispatcher (S3).

## Decisions
| ID | Decision |
|---|---|
| D1 | Slice by store, kernel first. Anything not bound fails closed: a commit carrying an evaluation plan returns 501 `P2_ORACLE_EVALUATION_NOT_BOUND`. |
| D2 | Reuse, don't copy, the existing shape validation: `TransactionalAuthorityStore.ValidateCommitShape` becomes `public static` (one-word change) so both stores enforce identical rules. |
| D3 | Same scenarios, two backends: in-memory is the reference; the Oracle store must produce identical codes (409/404/replay/rollback at every fault point). |
| D4 | Optimistic concurrency = `UPDATE … WHERE ENTITY_VERSION = :before`; 0 rows ⇒ 409 `P2_VERSION_CONFLICT`. Duplicate IDs are detected by unique constraints (ORA-00001 → mapped codes). Concurrent same-key commit: one retry, then replay/conflict. |
| D5 | Secrets: connection string only from `EIMS_ORACLE_CONNECTION` (environment/secret store), schema from `EIMS_ORACLE_SCHEMA` (default EIMS_OWNER, validated against a strict pattern). Application connects as EIMS_APP (DML only). |
| D6 | Provider: Oracle.ManagedDataAccess.Core (managed, no Oracle client install). Resolved version is recorded in the OracleBindingEvidence when S2 completes. |

## Known differences (accepted)
- Oracle compares IDs case-sensitively; the in-memory store treats aggregate IDs case-insensitively. Callers must use canonical IDs.
- Test rows remain in Oracle (EIMS_APP cannot delete audit rows by design); clean with `dev_rebuild_all.sql`.

## Acceptance
`dotnet run --project tests/EIMS.P2.OracleConformance.Tests` with `EIMS_ORACLE_CONNECTION` set prints ALL PASS, with every check present for both InMemory and Oracle.

## Known limits
Written without a compiler or Oracle available to the author; first build may need small fixes.
