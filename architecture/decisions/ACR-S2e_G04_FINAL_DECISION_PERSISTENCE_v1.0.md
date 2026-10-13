# ACR-S2e — G04 final decision persistence v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)

## Findings (record-vs-table comparison before any code)
- The final decision writes FOUR things at once: idea (state + technical state-mutation version), plan, assessment (`DECIDED`, decision version +1) and a 41-field evidence record. No table existed for the evidence record (the old `EIMS_G04_ASSESSMENT` decision columns were removed in S2c/S2d).
- The in-memory reference store is a single-instance holder (one idea/plan/assessment); it validates the commit with a private rule set that THROWS `InvalidOperationException` (`P1_G04_FINAL_*`) instead of returning a denial.
- Idempotency scope in-memory is (assessment, key); the stored record's aggregate is the idea.

## Decisions
| ID | Decision |
|---|---|
| D1 | New immutable table `EIMS_G04_FINAL_DECISION` mirrors `G04FinalDecisionEvidenceEnvelope` (41 columns), UNIQUE per assessment, FKs to assessment, plan and (nullable) committee snapshot, append-only trigger (-20051). Provenance columns are NOT NULL; free text (comment, rule-check results, specialist summary, profile JSON) is nullable and read back as ''. 20 tables in total. |
| D2 | One transaction: idea UPDATE → plan UPDATE → assessment UPDATE → evidence INSERT → audit → outbox → idempotency, with the 8 fault points of the in-memory store. Audit/outbox/idempotency reference the IDEA (as the envelopes do); idempotency key is unique per idea+command. |
| D3 | Concurrency: idea UPDATE predicates on BOTH `ENTITY_VERSION` (revision) and `NVL(STATE_MUTATION_VERSION, ENTITY_VERSION)` (technical version); plan on `PLAN_VERSION`; assessment on `DECISION_VERSION`; assessment and plan rows are locked `FOR UPDATE`. Any lost race → 409 `P1_G04_FINAL_CONCURRENT_CONFLICT`. |
| D4 | `G04FinalDecisionTransactionalStore.ValidateCommit` becomes `public static` (third one-word edit in this series) and is called before any write, so invalid commits throw the same `P1_G04_FINAL_*` exceptions on both stores. `MatchesCurrentRequest` is instance-bound and is re-implemented against DB state (12 comparisons) — the only duplicated rule; covered by the stale-context test. |
| D5 | Same codes as the in-memory store: `P1_G04_FINAL_DECISION_COMMITTED`, `P1_IDEMPOTENCY_CONFLICT`, `P1_G04_FINAL_CONCURRENT_CONFLICT`. |

## Out of scope
The route-integrity addendum (ACR-P0-007) and the evidence provider that resolves profile evidence are service-layer concerns and stay unchanged. Portfolio/execution/benefit/knowledge stores are S2f.

## Acceptance
Round-trip equality of idea, plan, assessment and the 41-field evidence; replay; stale-context and key-reuse denials; invalid-commit exception with nothing written; rollback at all 8 fault points; all earlier checks still pass (78 per backend).
