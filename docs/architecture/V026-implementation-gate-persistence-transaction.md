# V026 — Implementation Gate: Persistence Transaction Boundary

## Status
READY FOR CODING REVIEW — no application code changed.

## Finding

The current Core transaction contract:

```csharp
public interface IObservationTransaction
{
    T Execute<T>(Func<T> operation);
}
```

defines an application transaction boundary but does not provide a mechanism that guarantees that:

- observation persistence,
- audit append,
- idempotency state,

use the same database connection/transaction.

The current In-Memory implementation proves compensating behavior only. It is not evidence of Oracle atomicity.

## Constraint

Do not introduce Oracle-specific types into Core.

Do not create:
- `IOracleObservationRepository`
- `IOracleTransaction`
- Oracle-specific domain interfaces
- Oracle SQL/DDL

unless a later approved architecture decision explicitly requires them.

The existing `IObservationRepository` remains the repository contract.

## Required Infrastructure Decision

Before Oracle implementation, establish one Infrastructure-side unit-of-work/transaction mechanism capable of binding the repository, audit sink, and idempotency persistence to one database transaction.

The application boundary must be able to guarantee:

```text
BEGIN
  observation mutation
  + required audit
  + idempotency outcome
COMMIT
```

and:

```text
any failure before COMMIT
        ↓
ROLLBACK
        ↓
no partial persisted state
```

## TDD Gate

The implementation must begin with failing behavioral tests.

Required minimum behaviors:

1. mutation + audit failure → no committed mutation;
2. mutation + idempotency failure → no committed mutation;
3. same idempotency key concurrently → one committed mutation;
4. stale expectedVersion → no mutation and no success audit;
5. transaction commit → observation, audit and idempotency outcome visible together.

Tests must assert persisted state, not method-call choreography.

## Implementation Order

### Step 1 — RED
Add an Infrastructure-side test harness that can demonstrate the transaction boundary with a deterministic fake persistence store.

### Step 2 — GREEN
Implement the smallest transaction/unit-of-work boundary needed by the tests.

### Step 3 — REFACTOR
Ensure Core remains database-agnostic and the Infrastructure boundary owns database-specific concerns.

### Step 4 — Oracle Adapter
Only after the contract is GREEN:
- create the Oracle adapter implementing existing `IObservationRepository`;
- bind it to the approved transaction boundary;
- map Oracle/package errors to EIMS semantic errors;
- keep Oracle-specific details below the application boundary.

### Step 5 — Oracle Package/DB API
Only after adapter behavior is proven should exact Oracle package signatures and SQL/DDL be introduced under the V026 implementation gate.

## Explicit Non-Goals

This gate does not authorize:
- schema design;
- table creation;
- package body implementation;
- RLS/VPD implementation;
- connection pooling design;
- migrations;
- deployment scripts.

## Evidence Required Before V026 GREEN

The following evidence is mandatory:

- complete existing suite remains PASS;
- V026 transaction tests are PASS;
- concurrency/idempotency tests execute against the same persistence boundary;
- rollback is demonstrated by committed-state inspection;
- no Core dependency on Oracle exists.

Current evidence remains:

`53/53 PASS` for the existing VS01 suite.

Oracle atomicity remains **NOT PROVEN**.
