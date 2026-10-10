# EIMS — PROGRESS (give this file at the start of every new chat)

## Rule for every step
1) one branch `feature/sN-…`  2) short ACR first  3) one green test proves it  4) update Traceability Matrix + this file.

## Steps
| Step | Goal | Status |
|---|---|---|
| S1 | Oracle core schema V001 (5 tables) | **DONE** — verified on pilot Oracle (Oct 2026), all PASS |
| S1b | V002 evaluation, V003 G04, V004 waves 9–13 (12 tables) + ACR-S1b + conventions check | **DONE** — `dev_rebuild_all.sql`: 46/46 PASS, 17 tables, 4 append-only triggers |
| S2a | Oracle adapter, kernel commit path (V001 tables) + conformance suite | **DONE + MERGED** (PR #112, commit c49e9b7, 13 checks green); 16/16 PASS on InMemory and Oracle |
| S2b | Evaluation plan/assignments persistence + V002 realigned with C# envelopes (ACR-S2b) | **FILES READY — waiting for `dev_rebuild_all.sql` + conformance output (expect 24 PASS per backend)** |
| S2c | G04 voting/final decision (V003) | not started |
| S2d | Portfolio, Execution, Benefit, Knowledge stores (V004) | not started |
| S3 | Outbox dispatcher | not started |
| S4 | Bind g01.decide, g02.decide, rewards.decide to gateway + tests | not started |
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
- ACR-S2b: V002 was realigned field-by-field with EvaluationPlanEnvelope/EvaluationAssignmentEnvelope; state vocabularies are owned by code, not DB CHECKs (READY_FOR_G04_DECISION would have been rejected).
- ACR-S2a: slice the adapter per store; unbound parts fail closed (501); reuse ValidateCommitShape (public static); secrets only via environment.
- ACR-S1b: dedicated tables are authoritative; EIMS_AGGREGATE only for kernel entities without a table; audit/outbox/decision use soft references (option A).
- Oracle = reference DB; SQL Server later via same conformance suite.
- Frozen spec stays untouched; split tree is read-only (spec-tools/split_spec.py).
- SAP only through outbox + adapter, never DB-level.
- All VARCHAR2 use CHAR semantics (Persian text is 2 bytes/char).

## Open items
- Local git: stash `leftovers-before-pr112` can be dropped (`git stash drop`) once S2b is applied.
- Stale CI assertions were fixed in PR #112 (binding version >= 1.2.0, mutation count >= 29). Wave 14 test pins the current values.
- **Security:** the dev password for EIMS_APP was shared in a chat; rotate it to a random value before any real pilot. Never commit `EIMS_ORACLE_CONNECTION`.
- Commit `Directory.Packages.props` (repo root, Oracle.ManagedDataAccess.Core 23.26.301); remove the stray one on Desktop if unused.
- SDK 10.0.302 confirmed installed.
- Schema vs C# records is compared field-by-field in S2 via the conformance suite.
- ACR-S1b D4: once a pilot DB holds real data, migrations V001–V004 become immutable (changes = new V005+).
- IP ownership review (COM-LEGAL-001); frozen spec HTML not in repo.
