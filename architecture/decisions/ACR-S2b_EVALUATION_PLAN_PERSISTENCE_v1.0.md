# ACR-S2b — Evaluation plan/assignment persistence and V002 realignment v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)

## Finding
The first V002 did not match the C# envelopes: plan lacked CorrelationId, ReadyAt, DecisionRouteKind/Method, GovernanceProfileId/Version; assignment lacked IdeaId/IdeaVersion/Scope/CorrelationId and the completion fields; and a CHECK on plan state (ACTIVE/SUPERSEDED/COMPLETED/CANCELLED) would reject the state READY_FOR_G04_DECISION that the code sets at completion.

## Decisions
| ID | Decision |
|---|---|
| D1 | Per ACR-S1b D4 (dev phase) V002 is edited in place and rebuilt with dev_rebuild_all.sql. Columns mirror the envelopes one-to-one; unused columns (RULE_SET, PASSPORT_SNAPSHOT, SCORE, EVIDENCE, …) are removed. |
| D2 | State vocabularies belong to the domain code; the DB keeps only structural constraints (PK/UQ/FK, version ≥ 1, required flag 0/1). |
| D3 | `PLAN_VERSION` = plan-definition version from the envelope; `ENTITY_VERSION` = row concurrency token (used by S2c updates). For assignments `ENTITY_VERSION` = envelope `AssignmentVersion`. |
| D4 | CommitAsync persists plan + assignments in the same transaction as state/audit/outbox/idempotency. Duplicates map to 409 `P2_DUPLICATE_EVALUATION_PLAN` / `P2_DUPLICATE_EVALUATION_ASSIGNMENT_ID`. Fault points AfterEvaluationPlanStaged / AfterEvaluationAssignmentsStaged roll everything back. |
| D5 | Timestamps are read back as UTC via `TO_CHAR(SYS_EXTRACT_UTC(..))` (microsecond precision) so round-trip equality is exact and independent of driver time-zone handling. |
| D6 | Evaluation COMPLETION and G04 assessment stay fail-closed (501 / NotSupportedException) until S2c; V002 snapshot table is unchanged until then. |

## Acceptance
Round-trip equality of plan and both assignments on both backends; duplicate-plan rejection; rollback at both evaluation fault points; all earlier S2a checks still pass.
