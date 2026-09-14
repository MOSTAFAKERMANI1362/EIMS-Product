# ACR-P0-002 — G03 Need Lifecycle Domain Event Contracts

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen Product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` — unchanged

## Context

The frozen product and controlled P1 recovery now establish the final G03 state/rule semantics, but the missing historical P0 Event Catalog does not provide recoverable official G03 Domain Event identities. Prototype audit labels are not treated as Domain Event names.

This ACR creates a new controlled post-freeze event contract. It must never be described as recovery of an original event identity.

## Decision

| G03 semantic transition | Stable Domain Event | Routing role |
| --- | --- | --- |
| `needs.submit-g03`: `DRAFT → PENDING_G03_REVIEW` | `NeedSubmittedForG03Review.v1` | `NEED_REVIEWER` |
| `needs.g03-decision` APPROVE: `PENDING_G03_REVIEW → READY_FOR_IDEATION` | `NeedApprovedForIdeation.v1` | `IDEA_OWNER` |
| `needs.g03-decision` RETURN: `PENDING_G03_REVIEW → DRAFT` | `NeedReturnedFromG03Review.v1` | `NEED_OWNER` |

The final v6.360 behavior remains role-based. The removed `ideaOwnerCandidate` behavior is not restored.

## Delivery and envelope

Events are emitted only after a successful atomic commit through the Outbox. Delivery is at-least-once and consumers must be idempotent by `eventId`.

The standard envelope requires event identity/version/category, occurrence time, Need aggregate identity/version, actor identity, correlation ID and causation ID.

## Data minimization

The event payload contains only minimal routing context (`routingRole`). It does not duplicate the Need dossier (`title`, owner, current, desired, gap), and the G03 RETURN note text is not published in the integration event. Detailed content remains in authoritative EIMS domain state and append-only audit.

## Compatibility

The three v1 event types are immutable. Only backward-compatible optional payload additions are permitted within v1. Breaking semantics require a new major event contract.

## Non-effects

This ACR does not change G03 states, RuleSets, roles, SoD or routing semantics; does not bind a mutation planner; does not make a P1 Product command executable; and does not modify v6.360.

## P1 consumption gate

The existence of this ACR alone does not set `EventContractRecovered=true`. A separate controlled P1 binding must encode the outcome-aware allowed event set and validate planner output. `MutationContractRecovered` must remain false until the authoritative mutation planner is independently recovered and tested.
