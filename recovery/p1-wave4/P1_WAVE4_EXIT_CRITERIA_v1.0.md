# P1 Recovery Wave 4 — Exit Criteria v1.0

Wave 4 may merge only when all of the following are true:

- ACR-P0-003 remains the explicit POST_FREEZE source decision for G03 mutation semantics.
- Exactly two G03 commands are mutation-bound; no other Product command is promoted.
- Submit, APPROVE and RETURN mutations match ACR-P0-003 exactly.
- G03 event names remain those approved by ACR-P0-002.
- G03 review decision envelopes are server-stamped, append-only and transactionally atomic with state/audit/outbox/idempotency.
- RETURN note remains authoritative decision data and is not exposed through the current integration-event envelope.
- owner identity/role/scope are preserved; workflow routing is not authorization.
- optimistic concurrency, idempotency and rollback tests pass.
- SoD self-review denial passes.
- all existing P0/P1/P2 G03 regression gates pass.
- P5 command gateway remains fail-closed.
- Security Pipeline passes.
- build is 0 warnings / 0 errors for the Wave 4 test project.
- v6.360 is unchanged.

After merge, Issue #50 may be closed as logical Wave 4 complete. Physical Oracle/Domain binding and P5 activation remain separate future gates.