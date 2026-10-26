# ACR-S3 — Outbox dispatcher v1.0
Status: PROPOSED (accepted when dev_rebuild_all.sql is all PASS and the dispatcher conformance suite prints ALL PASS for InMemory AND Oracle)

## Context
Every Oracle store writes an `EIMS_OUTBOX` row (PENDING) in the same transaction as the state change. Nothing delivered those rows yet. S3 adds the worker that does, as the integration boundary towards SAP or a message bus (product principle: never DB-level integration).

## Decisions
| ID | Decision |
|---|---|
| D1 | New provider-neutral project `EIMS.Outbox`: contracts (`IOutboxStore`, `IOutboxPublisher`, `IOutboxInspector`), `OutboxDispatcher`, and `InMemoryOutboxStore` (reference + conformance baseline). The Oracle store is a partial of `OracleAuthorityStore` (reuses its connection/helpers). A SQL Server store later only has to pass the same suite. |
| D2 | Delivery is **at-least-once**; publishers/consumers must be idempotent on `MessageId`. Exactly-once is not claimed. |
| D3 | **Claim = one UPDATE statement** that stamps `CLAIM_TOKEN` + `CLAIMED_UNTIL` (lease) on the earliest due message of each aggregate; no row lock is held while publishing. Concurrent workers cannot double-claim (the UPDATE re-checks the lease predicate after waiting for a lock); a rare deadlock (ORA-00060) yields an empty batch and the worker simply polls again. |
| D4 | **Strict per-aggregate ordering**: a message is claimable only if no EARLIER message of the same aggregate is still PENDING or FAILED, so each claim holds at most one message per aggregate. A dead-lettered (FAILED) message blocks its aggregate until an operator calls `RequeueFailedAsync`; other aggregates keep flowing. |
| D5 | Retry policy: `ATTEMPTS` counts delivery attempts; failure n schedules `NextAttemptAt = now + BaseDelay·2^(n-1)` (capped at `MaxDelay`); the MaxAttempts-th failure (default 8), or a permanent failure, dead-letters (status FAILED). A publisher exception counts as a retryable failure. |
| D6 | Crash safety: after the lease expires an unfinished message is re-claimed with a new token; the stale claim can no longer finalize (`MarkSent/MarkFailed` check the token and return false). |
| D7 | `OutboxClaimFilter` (aggregate types) lets one worker/publisher serve only some types (e.g. SAP adapter for reward events). The tests rely on it to stay isolated from PENDING rows left by other test suites. |
| D8 | V005 adds `CLAIM_TOKEN`, `CLAIMED_UNTIL` and three indexes to `EIMS_OUTBOX`; the status CHECK (PENDING/SENT/FAILED) is unchanged. No new table (21 tables). EIMS_APP already holds UPDATE on the table. |
| D9 | The dispatcher is a plain class with `RunOnceAsync` and `RunAsync` (loop, survives store errors via `ErrorHandler`). Hosting as a `BackgroundService`, DI wiring and the concrete SAP/bus publishers belong to S4 and later. |

## Known limits (accepted)
- Throughput: one message per aggregate per claim; batches are limited by the number of distinct aggregates. Adequate for pilot volumes.
- `FOR UPDATE SKIP LOCKED` is not used (the single-statement claim avoids long transactions); revisit if many workers contend.
- Old PENDING rows written by earlier test suites are delivered like any other row when no filter is set; rebuild the dev DB to clear them.

## Acceptance
20 checks per backend: strict per-aggregate order and field fidelity, retry with doubling backoff, dead-letter + blocking + requeue, permanent failure, publisher exception, lease expiry + stale-claim rejection, two concurrent workers deliver each message exactly once, claim-filter isolation. The S2 conformance suite (154 per backend) still passes.
