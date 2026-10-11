# ACR-S2c — Evaluation completion, assessment snapshot and G04 assessment persistence v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)

## Findings (record-vs-table comparison before any code)
- `EIMS_EVALUATION_SNAPSHOT` was a generic "typed snapshot per plan" with UNIQUE(plan,type); the code stores ONE snapshot PER ASSIGNMENT (16 fields). A second required assignment would have failed.
- `EIMS_G04_ASSESSMENT` modelled voting/decision data (priority, rule set, assigned role, decision, reason…) with NOT NULL columns that `G04AssessmentEnvelope` does not carry, so the completion commit could not insert it.

## Decisions
| ID | Decision |
|---|---|
| D1 | Snapshot table mirrors `AssessmentSnapshotEnvelope`; UNIQUE(assignment); FKs to plan and assignment; JSON CHECK; immutable trigger kept. |
| D2 | G04 assessment table mirrors `G04AssessmentEnvelope` (+ENTITY_VERSION/UPDATED_AT), UNIQUE(plan), FK to plan. Voting/final-decision columns are added in S2d when that store is compared with its tables. Governance snapshot/vote tables keep their FK (ASSESSMENT_ID widened to 100 CHAR). |
| D3 | `CommitEvaluationCompletionAsync` = one transaction: assignment UPDATE → snapshot INSERT → plan UPDATE → optional G04 INSERT → audit → outbox (1–2) → idempotency. Same denial codes and fault points as the in-memory store. |
| D4 | Concurrency: optimistic version predicates on assignment (`ENTITY_VERSION`) and plan (`PLAN_VERSION`), plus `SELECT … FOR UPDATE` on the plan row so concurrent completions of one plan are serialized. A racing completion then fails readiness validation (500 `P2_EVALUATION_READINESS_MISMATCH`) instead of leaving a fully-completed plan stuck ACTIVE; the caller retries. |
| D5 | `ValidateEvaluationCompletionShape` becomes `public static` (second one-word change in TransactionalAuthorityStore.cs) so both stores enforce identical rules. |
| D6 | The idea aggregate is read and version-checked but NOT modified by completion (same as in-memory). |

## Acceptance
Round-trip equality of plan, assignment, snapshot and G04 assessment; idempotent replay; stale-plan conflict; non-required completion keeps plan ACTIVE; rollback at all 8 completion fault points; all S2a/S2b checks still pass (41 per backend).
