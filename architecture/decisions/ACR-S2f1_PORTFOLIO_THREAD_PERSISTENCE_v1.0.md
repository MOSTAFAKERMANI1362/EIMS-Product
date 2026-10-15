# ACR-S2f1 — Portfolio thread persistence and the shared Execution entity v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)

## Findings (record-vs-table comparison before any code)
- The Wave 9 portfolio store persists ONE aggregate, the **thread**, made of five records (candidate, assignment, membership, execution recommendation, execution hand-off) with a thread-level `ThreadVersion`. V004 had four generic tables: no assignment table, no thread version, and execution columns that did not match either execution envelope.
- The hand-off record (`ExecutionHandoffEnvelope`) is a subset of the Wave 10 `ExecutionEnvelopeWave10`: both are the same execution entity at different lifecycle stages.

## Decisions
| ID | Decision |
|---|---|
| D1 | Five tables mirror the thread: `EIMS_PORTFOLIO_CANDIDATE` (root, carries `THREAD_VERSION`, UNIQUE idea+approved version), `EIMS_PORTFOLIO_ASSIGNMENT`, `EIMS_PORTFOLIO_MEMBERSHIP`, `EIMS_EXECUTION_RECOMMENDATION`, `EIMS_EXECUTION`. At most one child of each kind per candidate (UNIQUE candidate id). 21 tables in total. |
| D2 | One entity, one table: `EIMS_EXECUTION` carries the FULL Wave 10 envelope (lifecycle columns defaulted). The portfolio hand-off INSERTs the row; the Execution store (S2f-2) UPDATEs it. The thread read returns the hand-off view of that row (state/version are therefore live after Execution moves on — accepted and tested in S2f-2). |
| D3 | Same transaction pattern as the other stores: thread write → audit → outbox (≥1) → idempotency, with the 5 Wave 9 fault points. Candidate row locked `FOR UPDATE` for every command; every child UPDATE is guarded by its own version; any lost race → 409 `P1_PORTFOLIO_CONCURRENT_CONFLICT`. |
| D4 | Idempotency scope as in-memory: eligibility = (command, idea, fingerprint); commands = (command, candidate, key). Audit/outbox reference the thread by `AGGREGATE_TYPE = PORTFOLIO_THREAD`. |
| D5 | `ValidateEligibilityCommit` and `ValidateCommandCommit` become `public static` (two one-word edits) and run before any write, so invalid commits throw the same `P1_PORTFOLIO_*` exceptions. |
| D6 | New defensive rule (not in the in-memory store): a command may not remove an existing child record; it throws `P1_PORTFOLIO_THREAD_SHRINK_NOT_SUPPORTED` before any write. |

## Out of scope
Execution lifecycle store (S2f-2); benefit and knowledge stores (S2f-3). `EIMS_BENEFIT.EXECUTION_ID` is only widened to 100 CHAR here.

## Acceptance
Eligibility creates the candidate; four commands grow the thread to the hand-off with field-for-field read-back after each; replay, stale-thread, key-reuse and invalid-version denials; rollback at all 5 fault points on both paths; all earlier checks still pass (102 per backend).
