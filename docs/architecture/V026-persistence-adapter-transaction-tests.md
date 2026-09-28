# EIMS — V026 Persistence Adapter / Transaction Contract Tests

Version: V026-TEST
Status: RED Contract Specification
Parent: V026 Oracle Package / DB API Boundary

## Purpose

This document defines the executable-test targets that must become GREEN before the Oracle implementation gate is opened.

The tests are behavior contracts. They must not assert class existence, method existence, HTTP 200, or Oracle-specific implementation details.

The existing `IObservationRepository` remains the persistence contract.

## Contract A — Repository Adapter

The future Oracle repository adapter must implement the existing repository semantics without changing the Core contract.

### V026-ORA-001 — Stable identifier read

Given an observation persisted through the adapter, `GetById(id)` returns the persisted observation.

Acceptance:
- same stable identifier resolves the same business observation;
- missing identifier returns null;
- no public API exposes Oracle table details.

### V026-ORA-002 — Version-guarded persistence

Given stored version N and a submitted observation with expectedVersion N:
- mutation succeeds;
- resulting version is persisted;
- resulting state is readable through the repository.

### V026-ORA-003 — Stale version rejection

Given stored version N and expectedVersion N-1:
- mutation is rejected as `EIMS_CONCURRENCY_CONFLICT`;
- stored observation remains unchanged;
- no success audit is produced.

### V026-ORA-004 — Version-guarded rollback safety

A compensating restore must never overwrite a state whose persisted version has advanced beyond the version produced by the failed operation.

This mirrors the already-green VS01 repository contract and must remain true for every adapter.

## Contract B — Transaction Atomicity

### V026-TX-001 — Business mutation and audit are atomic

Given:
1. observation mutation succeeds;
2. audit append fails before commit;

then:
- the transaction fails;
- the observation mutation is not committed;
- no partial business state remains.

The test must execute against the actual transaction implementation used by the adapter. An in-memory compensating rollback test is not sufficient evidence for Oracle atomicity.

### V026-TX-002 — Idempotency outcome is atomic with business mutation

Given an idempotent submission:
- business mutation and idempotency outcome commit together;
- if the transaction fails, neither is committed;
- retry can execute according to the idempotency contract.

### V026-TX-003 — Concurrent duplicate requests

Two concurrent requests with the same idempotency key and equivalent semantic request must not both execute the business mutation.

Acceptance:
- one committed mutation;
- subsequent duplicate replays the committed result;
- no duplicate business transition;
- no duplicate success outcome representing a second mutation.

## Contract C — Audit Boundary

### V026-AUD-001 — Audit is inside the same commit boundary

A successful business mutation cannot commit while its required audit append remains uncommitted.

### V026-AUD-002 — Failed command has no success audit

Concurrency, validation, authorization, or persistence failure must not leave a SUCCESS audit record.

The existing VS01 tests already prove this at the application/in-memory boundary; V026 must prove it at the persistence transaction boundary.

## Contract D — Error Mapping

Oracle-specific exceptions must be translated before reaching the public API.

At minimum:

| Condition | EIMS semantic |
|---|---|
| stale expected version | EIMS_CONCURRENCY_CONFLICT / HTTP 409 |
| idempotency semantic conflict | EIMS_IDEMPOTENCY_CONFLICT / HTTP 409 |
| validation failure | validation/domain error / HTTP 422 |
| unexpected database failure | internal failure / HTTP 500 |

Oracle error codes, package names, and table names must not become the public API contract.

## RED/GREEN Gate

The following distinction is mandatory:

- Existing VS01 In-Memory tests = GREEN evidence.
- V026 executable Oracle-boundary tests = NOT YET GREEN.
- Oracle transaction atomicity = NOT PROVEN until tests execute against the real Oracle adapter/transaction boundary.

A test that only checks that an Oracle class exists is not an acceptable V026 RED test.

## Implementation Gate

No Oracle SQL/DDL/package body is authorized by this document.

The next implementation step is:

1. define the smallest Infrastructure-side transaction/adapter contract needed to execute the above behaviors;
2. write executable RED tests against that contract;
3. implement the minimum adapter behavior;
4. run the complete existing suite;
5. only then introduce Oracle-specific implementation under the approved boundary.

## Traceability

V026 architecture contract:
`docs/architecture/V026-oracle-package-db-api-boundary.md`

Current evidence checkpoint:
`53/53 PASS`

That result proves the current in-memory/application contracts only. It does not prove Oracle atomicity.
