# EIMS — V026 Oracle Package / DB API Boundary

Version: V026  
Status: Design Contract

## Purpose

V026 defines the boundary between the EIMS application layer and Oracle. It does **not** authorize or contain executable Oracle SQL/DDL.

The V021 principles remain authoritative:

- Backend is authoritative.
- No direct Frontend → Oracle.
- No direct External System → EIMS Tables.
- No direct AI → Oracle.
- API → Application Service → Authorization → Domain → Repository → Oracle.
- Tables are Source of Truth; Views are Read Models / Projections.
- Sensitive state changes are command/use-case oriented.
- Optimistic concurrency is required where applicable.
- Important state-changing commands require idempotency.
- Audit is append-oriented and backend-enforced.
- SYS/SYSTEM are not normal application principals.

## Boundary

```
API
  ↓
Application Service
  ↓
Repository Contract
  ↓
Oracle Repository Adapter
  ↓
EIMS_APP / Oracle Package API
  ↓
EIMS_OWNER tables
```

The application must not depend on Oracle SQL statements or table mutation details directly.

## Oracle Principals

V021 defines EIMS_OWNER, EIMS_APP, EIMS_READONLY and EIMS_MIGRATION. V026 does not create these principals or grant privileges.

## Package Boundary

Business-sensitive database operations should use explicit package operations rather than generic table mutation.

Initial categories:

- Observation: submit for G01, persist under expected-version guard, read by identifier
- Audit: append audit event
- Idempotency: atomically claim/replay an idempotent command

Exact package names and signatures remain an implementation-gate decision.

## Transaction Contract

For Observation → G01:

```
Begin transaction
  ├─ validate expected version
  ├─ persist observation transition
  ├─ persist required business side effects
  ├─ append required audit
  ├─ persist idempotency outcome
Commit
```

Any failure before Commit must roll back the database transaction.

The current In-Memory implementation uses version-guarded compensating rollback. That is **not equivalent** to Oracle transaction atomicity.

Therefore:

- In-Memory compensation = currently proven
- Oracle transaction atomicity = not yet proven

## Concurrency Contract

1. Caller supplies expected business version.
2. Stored version must match.
3. Successful transition increments business version.
4. Stale version causes a conflict.
5. Conflict creates no success audit.
6. Conflict creates no partial business mutation.

The API may carry the version through `If-Match`; the Repository may use `expectedVersion`.

## Idempotency Contract

Sensitive commands must be safe under retry:

- same key + same semantic request replays the original result;
- same key + different semantic request is rejected;
- key claiming is atomic;
- concurrent duplicates cannot both execute the mutation;
- retention covers the real retry/replay window.

A non-atomic read-then-insert check is not sufficient.

## Audit Contract

Audit remains distinct from Decision, Evidence and TraceLink.

For sensitive commands:

- actor comes from server-derived authenticated context;
- command identity is explicit;
- affected entity is recorded;
- outcome is recorded;
- resulting business version is recorded where applicable;
- audit is append-oriented;
- ordinary application operations cannot overwrite/delete audit history.

## Authorization / Session Context

The application derives authenticated principal, active role, effective capability, effective scope, valid business state and valid workflow transition.

Oracle receives only the approved server-derived session/security context.

Exact RLS/VPD implementation remains open, as stated by V021.

## Error Mapping

Oracle-specific errors must not become the public API contract.

Relevant application semantics:

- 401 — unauthenticated
- 403 — forbidden/scope violation
- 404 — not found
- 409 — concurrency/idempotency conflict
- 422 — validation failure
- 500 — unexpected internal/database failure

## Source of Truth

Production Oracle tables remain the Source of Truth.

Oracle views are projections/read models.

Packages are the controlled DB API boundary; package access does not replace application authorization.

## VS-01 Traceability

Verified current checkpoint:

```
Contract Tests: 53/53 PASS
```

Relevant contracts include persistence, optimistic concurrency, idempotency, transaction failure compensation, version-guarded rollback, audit enforcement and repository behavior.

Interpretation:

- In-Memory persistence contract = GREEN
- Version-guarded compensation = GREEN
- Oracle transaction atomicity = NOT YET PROVEN

## Deferred

- Oracle SQL/DDL
- exact table columns
- exact package names/signatures
- exact RLS/VPD implementation
- connection/session-pooling details
- migrations
- deployment topology
- backup/DR
- production observability
- performance/load limits

## Acceptance Criteria

V026 is accepted when the Oracle boundary, package intent, transaction atomicity, concurrency, idempotency, audit and authorization/session-context requirements are explicit; Oracle-specific errors are not public API semantics; deferred items are explicit; and no SQL/DDL is introduced before implementation authorization.

## Next Step

Before Oracle SQL/DDL:

1. Freeze/review V026 boundary.
2. Define the Oracle adapter contract against the existing Repository contract.
3. Add RED contract tests for Oracle-boundary behavior using a test double.
4. Only then authorize the Oracle implementation gate.
