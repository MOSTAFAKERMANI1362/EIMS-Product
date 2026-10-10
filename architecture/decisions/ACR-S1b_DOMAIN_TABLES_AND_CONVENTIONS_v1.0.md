# ACR-S1b — Domain tables, authority split and schema conventions v1.0
Status: PROPOSED (accepted when `dev_rebuild_all.sql` shows no FAIL and no ORA- error on the pilot Oracle)
Supersedes: ACR-S1 D6 (deferred FKs from audit/outbox/decision to EIMS_AGGREGATE).

## Context
V001 created generic tables (EIMS_AGGREGATE + audit/outbox/decision/idempotency). V002–V004 added dedicated tables for Evaluation, G04, Portfolio, Execution, Benefit and Knowledge. Two state stores risked split-brain, and V002–V004 used different conventions from V001.

## Decisions
| ID | Decision |
|---|---|
| D1 | **Authority split.** Dedicated tables (V002–V004) are the only source of truth for their stages. EIMS_AGGREGATE holds kernel entities that have no dedicated table yet (ImprovementSource, Case, Need, Idea). One entity never lives in both. |
| D2 | **Soft reference.** EIMS_AUDIT_LOG, EIMS_OUTBOX and EIMS_DOMAIN_DECISION reference an entity by (AGGREGATE_TYPE, AGGREGATE_ID) with no FK, because the target can be any table. Integrity = same-transaction write by the adapter + a Persistence Conformance test (S2) asserting every referenced entity exists. |
| D3 | **Conventions (all tables):** VARCHAR2 uses CHAR semantics (Persian = 2 bytes/char); times are TIMESTAMP(6) WITH TIME ZONE, UTC from the app; concurrency column is ENTITY_VERSION; every PK is named `PK_<table>`; JSON only in CLOB with `IS JSON`. Enforced by `verify/verify_conventions.sql`. |
| D4 | **Dev-phase rule.** Until the first pilot database holds real data, migrations V001–V004 may be edited in place and the DB rebuilt with `dev_rebuild_all.sql`. From then on migrations are immutable: change = new V00N file. |
| D5 | Cross-stage references such as IDEA_ID stay soft (no FK) for now; revisit when Idea gets its dedicated table. |

## Acceptance
`dev_rebuild_all.sql` output contains only PASS lines in every verify block and no ORA- error except the harmless ORA-00942 in the RESET block on a first run.

## Known limits
Written without a live Oracle; the V002–V004 changes were applied mechanically (rename, type and PK-name rewrites) and syntax-checked only for parentheses until the acceptance run.
