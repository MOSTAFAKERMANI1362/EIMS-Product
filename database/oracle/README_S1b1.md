# EIMS — S1b-1 Evaluation Core

Scope:
- Evaluation Plan
- Evaluation Assignment
- immutable Evaluation Snapshot

Source-backed contract extracted from the v6.360 prototype:
- an evaluation plan is bound to a specific Idea version;
- submission freezes/increments the Idea version before plan creation;
- assignments are role/misson based and carry status;
- required assignments gate G04 readiness;
- the plan carries a decision route and rule-set identity;
- a frozen profile/snapshot is retained as evaluation context;
- the same plan must not be recreated for the same Idea version;
- snapshots are evidence and are immutable.

P1/implementation boundary:
- UI-only behavior is not persisted here.
- Exact G04 decision semantics remain outside S1b-1.
- Oracle adapter and physical transaction behavior are S2 work.
- Runtime application account is EIMS_APP; schema ownership remains EIMS_OWNER.

Run:
1. Connect as EIMS_OWNER to FREEPDB1.
2. `@migrations/V002__evaluation_core.sql`
3. `@verify/verify_evaluation_core.sql`

Expected verification:
- 3 evaluation-core tables exist
- optimistic assignment update passes
- stale assignment version is rejected
- duplicate Idea/version plan is rejected
- snapshot UPDATE and DELETE are blocked
- duplicate snapshot type is rejected
- invalid JSON is rejected
- test rows are rolled back
