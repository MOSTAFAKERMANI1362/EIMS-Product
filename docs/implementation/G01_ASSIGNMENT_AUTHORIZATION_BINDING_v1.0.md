# G01 Assignment + Authorization Binding — v1.0

## Status

IMPLEMENTED ON `feature/vs01-observation-g01` — contract binding only.

## Bound behavior

- `G01WorkAssignment` may carry the server-bound `AssignedPrincipalId`.
- A pending G01 assignment can be accepted with a server-supplied principal; that principal becomes the assignment owner.
- Only the bound principal may start the assignment.
- G01 decision authorization requires authenticated server principal, `INTAKE_STEWARD`, `G01.DECIDE`, valid scope, `SubmittedForG01`, matching Observation assignment, `INTAKE_STEWARD` assignment role, `InProgress`, owner == authenticated principal, and submitter != decision actor when `SubmittedByPersonId` is available.
- `SubmittedByPersonId` is captured from the server security context at the application submission boundary.
- Client-supplied role/capability values remain ignored.

## Reuse boundary

Reuses `G01WorkAssignment`, `ObservationSecurityContext`, `ObservationAuthorizationService`, `ObservationSubmissionService`, and `ObservationApplicationService`.

No new database abstraction, Oracle type, schema, table, migration, or persistence adapter was introduced.

## Explicit non-claims

This package does not prove Oracle persistence, production transaction atomicity, G01 rule execution, G01 gate evaluation, Decision Snapshot, Decision Idempotency, final Observation post-decision state, reason-code catalog, or R04/R05 executable evaluators.

## Test artifact

`tests/EIMS.G01.AssignmentAuthorization.ContractTests/` contains assignment ownership, status, Observation binding, capability, role, scope, self-approval, and owner-lifecycle cases.

## Verification limitation

The GitHub connector did not expose a workflow run for the feature-branch commits, and the execution environment cannot clone the repository because external network resolution is unavailable. Therefore no local/CI PASS claim is made here.
