# EIMS — PROGRESS (give this file at the start of every new chat)

## Rule for every step
1) one branch `feature/sN-…`  2) short ACR first  3) one green test proves it  4) update Traceability Matrix + this file.

## Steps
| Step | Goal | Status |
|---|---|---|
| S1 | Oracle core schema V001 (5 tables) | **DONE** |
| S1b | V002 evaluation, V003 G04, V004 waves 9–13 + ACR-S1b + conventions check | **DONE** |
| S2a–S2e | Oracle stores: kernel commit, evaluation plan/completion, G04 voting, G04 final decision (ACR-S2a…S2e) | **DONE + MERGED** (conformance 16→78 per backend) |
| S2f-1/2/3 | Portfolio thread, Execution lifecycle, Benefit + Knowledge on Oracle (ACR-S2f1…S2f3) | **DONE + MERGED** — conformance 154/154 PASS on InMemory and Oracle; 21 tables |
| S2f-2b | Execution hand-off INTAKE store (system, event-driven) | optional — only if events cross databases; decide with pilot topology |
| S3 | Outbox dispatcher: `EIMS.Outbox` (contracts, dispatcher, in-memory reference) + Oracle claim/lease store + V005 (ACR-S3) | **FILES READY — waiting for `dev_rebuild_all.sql` + dispatcher suite (expect 20 PASS per backend) + S2 suite still 154** |
| S4 | Wire everything into the Host: bind g01.decide, g02.decide, rewards.decide to the gateway, Oracle stores via DI (OracleBindingEvidence), dispatcher as BackgroundService | not started |
| S5 | Query API (worklist, ideas) → migrate UI pages in matrix order | not started |

## Oracle binding evidence (for OracleBindingEvidence)
- OracleVersion: Oracle AI Database 26ai Free 23.26.3
- DotNetProvider: Oracle.ManagedDataAccess.Core 23.26.301 (managed)
- ConnectivityMode: TCP localhost:1521/FREEPDB1
- ServiceAccountModel: EIMS_APP (DML only) / schema owner EIMS_OWNER
- EvidenceReference: S2a conformance run, Oct 2026

## Environment (confirmed)
Oracle AI Database 26ai Free 23.26.3, PDB FREEPDB1, accounts EIMS_OWNER / EIMS_APP.

## Decisions log
- ACR-S3: at-least-once delivery, strict per-aggregate ordering, FAILED blocks its aggregate until requeued, claim = single UPDATE with token+lease, claim filter by aggregate type, hosting deferred to S4.
- ACR-S2e: final decision evidence is an immutable per-assessment table; idea UPDATE guarded by revision AND technical state-mutation version; assessment/plan rows locked FOR UPDATE; ValidateCommit made public static; MatchesCurrentRequest re-implemented against DB state (only duplicated rule).
- ACR-S2d: votes reference the G04 assessment as the aggregate; committee snapshot/state/vote revision/effective vote are four tables (19 tables total); assessment row locked FOR UPDATE during a vote; ValidateCommitShape and SameOptionalSnapshot made public static.
- ACR-S2c: snapshot is per assignment (not per plan/type); G04 assessment table mirrors G04AssessmentEnvelope; plan row is locked FOR UPDATE during completion; ValidateEvaluationCompletionShape made public static.
- ACR-S2b: V002 was realigned field-by-field with EvaluationPlanEnvelope/EvaluationAssignmentEnvelope; state vocabularies are owned by code, not DB CHECKs (READY_FOR_G04_DECISION would have been rejected).
- ACR-S2a: slice the adapter per store; unbound parts fail closed (501); reuse ValidateCommitShape (public static); secrets only via environment.
- ACR-S1b: dedicated tables are authoritative; EIMS_AGGREGATE only for kernel entities without a table; audit/outbox/decision use soft references (option A).
- Oracle = reference DB; SQL Server later via same conformance suite.
- Frozen spec stays untouched; split tree is read-only (spec-tools/split_spec.py).
- SAP only through outbox + adapter, never DB-level.
- All VARCHAR2 use CHAR semantics (Persian text is 2 bytes/char).

## Open items
- Main moved during S2 (ACR-P0-004 idea-evaluation-submission contract tests added in PR #53): after S2c merges, run those contract tests once against the Oracle-backed flow.
- Correction: earlier notes said '46/46' DB checks for S1b; the correct count at that time was 41 (7+8+7+12+7). Do not confuse DB checks (verify scripts) with conformance checks (C#).
- Local git: stash `leftovers-before-pr112` can be dropped (`git stash drop`) once S2b is applied.
- Stale CI assertions were fixed in PR #112 (binding version >= 1.2.0, mutation count >= 29). Wave 14 test pins the current values.
- **Security:** the dev password for EIMS_APP was shared in a chat; rotate it to a random value before any real pilot. Never commit `EIMS_ORACLE_CONNECTION`.
- Commit `Directory.Packages.props` (repo root, Oracle.ManagedDataAccess.Core 23.26.301); remove the stray one on Desktop if unused.
- SDK 10.0.302 confirmed installed.
- Schema vs C# records is compared field-by-field in S2 via the conformance suite.
- ACR-S1b D4: once a pilot DB holds real data, migrations V001–V004 become immutable (changes = new V005+).
- IP ownership review (COM-LEGAL-001); frozen spec HTML not in repo.
