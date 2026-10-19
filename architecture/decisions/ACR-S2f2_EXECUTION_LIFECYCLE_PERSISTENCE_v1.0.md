# ACR-S2f2 — Execution lifecycle persistence and the shared Benefit entity v1.0
Status: ACCEPTED - dev_rebuild_all.sql all PASS; InMemory and Oracle conformance ALL PASS (154/154 per backend, exit code 0; 2026-10-19)
Builds on ACR-S2f1 (shared `EIMS_EXECUTION` row).

## Findings (record-vs-table comparison before any code)
- The Execution thread is `ExecutionEnvelopeWave10` (20 fields) plus an optional `BenefitObligationEnvelopeWave10` created by the completion review. The obligation is the first stage of the Wave 11 `BenefitEnvelopeWave11` (21 fields): same entity, different lifecycle stage.
- The old `EIMS_BENEFIT` (title, status CHECK list, dossier CLOBs, KPI, thread id …) matched neither envelope; its state CHECK list belonged in domain code.
- The in-memory `ExecutionRuntimeStoreWave10` additionally implements a system-only hand-off INTAKE (materializes an execution from the Wave 9 hand-off event). With the shared row the hand-off already inserts the execution.

## Decisions
| ID | Decision |
|---|---|
| D1 | `EIMS_BENEFIT` is rebuilt to mirror `BenefitEnvelopeWave11` (UNIQUE execution id, FK to `EIMS_EXECUTION`, no state CHECK). The Execution completion INSERTs the obligation (subset of columns); the Benefit store (S2f-3) updates the same row. The verify test "invalid Benefit state rejected" is removed (vocabulary is domain-owned). |
| D2 | One transaction: execution UPDATE (`ENTITY_VERSION` predicate, `UPDATED_AT` written from the envelope so read-back is exact) → optional obligation INSERT/guarded UPDATE → audit → outbox → idempotency, with the 5 Wave 10 fault points. The execution row is locked `FOR UPDATE`. Lost race → 409 `P1_EXECUTION_CONCURRENT_CONFLICT`. |
| D3 | `ExecutionTransactionalStoreWave10.ValidateCommit` becomes `public static` (one-word edit) and runs before any write (same `P1_EXECUTION_*` exceptions). New defensive rule: a command may not drop an existing obligation (`P1_EXECUTION_THREAD_SHRINK_NOT_SUPPORTED`). |
| D4 | Idempotency scope as in-memory: (command, execution, key). Audit/outbox reference `AGGREGATE_TYPE = EXECUTION`. |
| D5 | **Follow-up S2f-2b (not in this step):** `IExecutionHandoffIntakeStoreWave10` is not bound on Oracle. It is only needed if execution rows must be materialized from hand-off EVENTS (distributed deployment); in the single-database design the portfolio hand-off already inserts the row. Decide with the pilot topology. |

## Acceptance
Five lifecycle commands (charter, plan, progress, completion submit, completion review incl. obligation) each read back field-for-field; replay, stale-version, key-reuse and invalid-version denials; rollback at all 5 fault points; all earlier checks still pass (116 per backend).
