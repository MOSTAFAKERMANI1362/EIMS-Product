# ACR-S2f3 — Benefit and Knowledge persistence v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)
Builds on ACR-S2f2 (shared `EIMS_BENEFIT` row created by the execution completion).

## Findings (record-vs-table comparison before any code)
- `EIMS_KNOWLEDGE_ASSET` (title, status CHECK, content CLOB …) matched none of the 19 fields of `KnowledgeEnvelopeWave13` (benefit/execution/idea thread ids, author policy and scope, validation and publication actors, dossier ref, created/updated).
- The Benefit store has a system INTAKE (`MaterializeBenefitObligationAsync`) in addition to commands; with the shared row it is normally an "already materialized" no-op.
- Knowledge create and knowledge mutation use different idempotency scopes in the in-memory store (benefit vs knowledge id).

## Decisions
| ID | Decision |
|---|---|
| D1 | `EIMS_KNOWLEDGE_ASSET` mirrors `KnowledgeEnvelopeWave13`: UNIQUE benefit id (one asset per benefit), FK to `EIMS_BENEFIT`, no state CHECK (vocabulary is domain-owned). `UPDATED_AT` is NOT NULL and written from the envelope. |
| D2 | Benefit commands: one transaction = benefit UPDATE (`ENTITY_VERSION` predicate, row locked FOR UPDATE, `UPDATED_AT` from the envelope) → audit → outbox → idempotency, 5 Wave 11 fault points; 409 `P1_BENEFIT_CONCURRENT_CONFLICT`. Intake: idempotent; ALREADY_MATERIALIZED if the shared row matches the source, materializes the row (+audit) only if absent; FK failure → 409 `P1_BENEFIT_INTAKE_THREAD_CONFLICT`. |
| D3 | Knowledge create: duplicate-benefit and duplicate-id checks (also enforced by the unique constraints) → INSERT → audit → outbox → idempotency scoped to the BENEFIT; knowledge mutation: scoped to the KNOWLEDGE id, row locked, version predicate. Same codes and 5 fault points as the in-memory store. |
| D4 | Shape rules are reused, not copied: `BenefitTransactionalStoreWave11.ValidateCommit`, `KnowledgeTransactionalStoreWave13.ValidateCreate` and `ValidateMutation` become `public static` (three one-word edits). |
| D5 | Accepted difference: knowledge create against a benefit that does not exist is rejected by the FK on Oracle (the in-memory store has no such check; the service layer validates it). |

## Acceptance
Benefit: intake no-op/replay/conflict, five commands read back field-for-field, replay, stale, key-reuse, invalid-version denials, 5 rollbacks. Knowledge: create + reads by id and by benefit, replay, one-asset-per-benefit, validate/publish read-back, stale, key reuse, invalid create/mutation, 10 rollbacks. All earlier checks still pass (154 per backend).
