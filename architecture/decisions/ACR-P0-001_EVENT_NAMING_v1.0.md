# ACR-P0-001 — G01→Case / G02→Need Event Naming

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen Product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` — unchanged

## Context

The frozen product establishes the business semantics for G01 approval creating a `Case` and G02 qualification creating a `Need`, but explicitly leaves the official Domain Event mapping TBD. The P0 Gaps register requires a stable versioned event identity through ACR before P1. EP-01A defines the event naming form as entity + past-tense action and at-least-once delivery with idempotent consumers.

This ACR resolves only that naming gap. It is a **new post-freeze architecture decision**, not a claim that these names existed in the original v6.360/P0 machine package.

## Decision

| Transition | Stable Domain Event |
| --- | --- |
| G01 `APPROVE` creates Case in `UNDER_REVIEW` | `CaseCreatedFromApprovedSource.v1` |
| G02 `NEED_CANDIDATE` creates Need in `DRAFT` | `NeedCreatedFromQualifiedCase.v1` |

The names follow the candidates explicitly documented in `EIMS_P0_Gaps_TBD_v1.0.md` and the EP-01A naming standard.

## Delivery contract

Events are written through the same atomic transaction/outbox boundary as the successful aggregate mutation. Publication is at-least-once; consumers must deduplicate by `eventId`.

The event envelope retains the standard identity/version/correlation fields. Payload is deliberately minimal: the source aggregate ID and the newly created aggregate ID. Business snapshots are not duplicated into the event unless a later compatible contract requires them.

## Compatibility

The v1 event type is immutable. Backward-compatible optional payload fields may be added within v1. A breaking semantic change requires a new major version/contract.

## Non-effects

This ACR does not change G01/G02 decisions, UIC-R027, roles, frozen state semantics, thresholds, or v6.360. It also does not by itself make either P1 command executable. Complete state/rule/mutation contracts and the existing server-authority, concurrency, idempotency and atomic-persistence gates remain mandatory.
