# EIMS P1 Recovery Wave 3 — G03 Event Rebaseline v1.0

**Status:** APPROVED REBASELINE FOR P1 EVENT BINDING  
**Decision class:** RECOVERY_REBASELINE_ACCEPTANCE  
**Source architecture decision:** `ACR-P0-002`  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`

## Purpose

Wave 3 consumes the approved post-freeze G03 Domain Event contracts from `ACR-P0-002` inside the P1 recovery catalog. This is not a claim that the historical P0 Event Catalog has been recovered.

## Accepted event bindings

- `needs.submit-g03` uses one static event: `NeedSubmittedForG03Review.v1`.
- `needs.g03-decision` is explicitly outcome-aware:
  - `APPROVE` → `NeedApprovedForIdeation.v1`
  - `RETURN` → `NeedReturnedFromG03Review.v1`

There is no default or fallback Domain Event for an unknown or missing decision outcome.

## Runtime safety

The two G03 commands now have complete recovered/rebaselined State, Rule and Event contracts. Their Mutation contracts remain deliberately unrecovered.

The P1 Authority Kernel must therefore fail closed at `P1_MUTATION_CONTRACT_NOT_RECOVERED` before Store, Rule Evaluator, SoD evaluator or Mutation Planner is touched. Event binding by itself does not emit an event, change state, or authorize a command.

## Event contract representation

`CommandPolicy` now supports an explicit `CommandEventBinding`:

- `STATIC` for commands with one event identity;
- `OUTCOME` for commands whose Domain Event depends on a validated business outcome.

The outcome resolver is case-insensitive but fail-closed: a missing or unknown outcome returns no event identity.

## Delivery semantics

The accepted events retain the ACR-P0-002 contract:

- at-least-once delivery;
- event emission only after successful atomic commit via Outbox;
- consumer idempotency by EventId;
- minimal routing payload;
- no Need dossier or G03 return-note text in the integration event.

## Non-goals

This wave does not bind a mutation planner, does not enable any Product command, does not change G03 rules/states/roles, does not bind G01/G02/G04 events, and does not modify v6.360.

## Next gate

Only a separately controlled G03 Mutation Contract rebaseline may move the two commands beyond the Mutation gate. That future wave must define exact post-state, routing/assignment side effects, audit/outbox composition and outcome-to-event use, with atomicity and idempotency tests before any command can execute.
