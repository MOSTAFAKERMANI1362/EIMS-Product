# ACR-S2d — G04 committee voting persistence v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the conformance suite prints ALL PASS for InMemory AND Oracle)

## Findings (record-vs-table comparison before any code)
The G04 voting store persists FOUR envelopes — committee snapshot (with a member list), committee state (optimistic `CommitteeVersion`), vote revision (append-only history) and effective vote (per-member projection) — plus audit/outbox/idempotency. V003 had two generic tables (`GOVERNANCE_SNAPSHOT`, `COMMITTEE_VOTE`) that could not hold any of them (no member list, no revision chain, no committee version, one vote per member only).

## Decisions
| ID | Decision |
|---|---|
| D1 | V003 voting tables are replaced by four tables mirroring the envelopes: `EIMS_G04_COMMITTEE_SNAPSHOT` (immutable, members as JSON CLOB), `EIMS_G04_COMMITTEE_STATE` (PK = assessment, `COMMITTEE_VERSION` token), `EIMS_G04_VOTE_REVISION` (append-only, UNIQUE assessment+person+revision, self-FK for supersedes), `EIMS_G04_EFFECTIVE_VOTE` (PK assessment+person). 19 tables in total. |
| D2 | Votes use the G04 ASSESSMENT as the aggregate for audit/outbox/idempotency (command `g04.vote`), exactly like the in-memory store; `AGGREGATE_TYPE` = `G04_ASSESSMENT`. |
| D3 | One transaction: [snapshot on first vote] → vote revision → effective vote (UPDATE, else INSERT) → committee state (INSERT or UPDATE … WHERE COMMITTEE_VERSION = :before) → audit → outbox (1–2) → idempotency. Same denial codes and the 8 voting fault points of the in-memory store. |
| D4 | The G04 assessment row is locked `FOR UPDATE` for the whole vote, so concurrent votes on one assessment are serialized; stale contexts get 409 (`P2_G04_COMMITTEE_CONTEXT_CHANGED` / `…_VERSION_CONFLICT`). |
| D5 | `G04VotingTransactionalStore.ValidateCommitShape` and `SameOptionalSnapshot` become `public static` (two one-word edits) so both stores enforce identical rules instead of a copied rule set. |
| D6 | Known differences (accepted): Oracle compares person ids case-sensitively (in-memory: case-insensitive); empty approval authority/reference are stored as NULL and read back as empty. |

## Out of scope
G04 final decision (idea/plan/assessment updates + 40-field evidence record) is S2e; portfolio/execution/benefit/knowledge is S2f.

## Acceptance
Round-trip equality of snapshot, state and effective votes; replay; revision chain; completion at quorum; denial after completion; stale-context denial; rollback at all 8 voting fault points; all earlier checks still pass (60 per backend).
