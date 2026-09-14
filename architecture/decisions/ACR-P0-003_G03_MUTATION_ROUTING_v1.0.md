# ACR-P0-003 — G03 Mutation, Review Status and Work Routing Contract v1.0

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## 1. Purpose and provenance

The historical standalone P0 mutation/state/event catalog is not physically available in the controlled repository. This ACR therefore does **not** claim historical recovery. It stabilizes the G03 mutation contract as a controlled post-freeze architecture decision using the frozen v6.360 behavior, recovered G03 state/rule evidence, and the already approved ACR-P0-002 event identities.

`v6.360` is not modified by this decision.

## 2. `needs.submit-g03`

A valid server-authorized submission performs exactly one Need aggregate mutation:

- `DRAFT` → `PENDING_G03_REVIEW`;
- aggregate version increments by exactly one;
- `g03ReviewStatus` becomes `PENDING`;
- `workRoutingRole` becomes `NEED_REVIEWER`;
- event identity is `NeedSubmittedForG03Review.v1`;
- Need owner identity and Need scope are preserved.

Submission does not create a G03 review-decision record because no reviewer decision has occurred yet.

## 3. `needs.g03-decision` — APPROVE

The command is valid only for a Need in `PENDING_G03_REVIEW` whose authoritative `g03ReviewStatus` is `PENDING` and after the recovered G03 rule/SoD checks pass.

Successful APPROVE performs:

- `PENDING_G03_REVIEW` → `READY_FOR_IDEATION`;
- version +1;
- `g03ReviewStatus=APPROVED`;
- `workRoutingRole=IDEA_OWNER`;
- event `NeedApprovedForIdeation.v1`;
- append-only `G03ReviewDecision` record containing the reviewer, assignment, entity version, correlation, timestamp, decision and the three approved controls.

A return note is not part of an APPROVE record.

## 4. `needs.g03-decision` — RETURN

Successful RETURN performs:

- `PENDING_G03_REVIEW` → `DRAFT`;
- version +1;
- `g03ReviewStatus=RETURNED`;
- `workRoutingRole=NEED_OWNER`;
- event `NeedReturnedFromG03Review.v1`;
- append-only `G03ReviewDecision` record containing the actionable return note and reviewer authority context.

The return note must be at least 10 characters after trimming. It is authoritative domain/decision data and stays in the domain/audit persistence boundary. Its text must not be copied into the integration event.

## 5. Work routing is not authorization

`workRoutingRole` is a **WORKFLOW_ROUTING_FACT**. It tells the worklist which workflow role queue owns the next mission. It is server-computed and must never be treated as an authorization grant.

Changing `workRoutingRole` must not create or modify:

- `RoleAssignmentEntry`;
- Windows claims;
- `PersonDirectoryEntry`.

Authorization continues to require a valid server-resolved PersonID, Role, Scope and Assignment through P3. A client-supplied routing role, state, review status or event type is never trusted.

## 6. Immutable G03 review history

APPROVE and RETURN create a new append-only `G03ReviewDecision`. Historical decisions are not overwritten on return/resubmission/re-review.

The decision record is part of the same authoritative transaction as the Need mutation. For RETURN, the note is stored in this decision record but excluded from the integration event payload.

## 7. Atomic persistence boundary

A successful mutation must commit, in one transaction:

1. Aggregate State/Version/authoritative routing facts;
2. `G03ReviewDecision` when the command is a reviewer decision;
3. Audit;
4. Outbox;
5. Idempotency record.

Any failure rolls back the entire transaction. Outbox publication occurs only after commit. Optimistic concurrency and server-side idempotency remain mandatory.

## 8. Event resolution

Event identities are those already stabilized by ACR-P0-002:

- submit → `NeedSubmittedForG03Review.v1`;
- APPROVE → `NeedApprovedForIdeation.v1`;
- RETURN → `NeedReturnedFromG03Review.v1`.

The outcome-aware decision command has no generic or fallback event. Missing or unknown outcomes remain fail-closed.

## 9. P1 consumption gate

This ACR defines the mutation contract but **does not** set `MutationContractRecovered=true` by itself. A separate controlled P1 recovery wave must first provide and verify:

- pure G03 mutation planner;
- exact outcome/event resolution;
- immutable decision-record persistence contract;
- atomic commit/rollback behavior;
- authorization, SoD, concurrency and idempotency tests;
- controlled P5 composition decision.

Until that wave passes, the two G03 Product commands continue to stop at `P1_MUTATION_CONTRACT_NOT_RECOVERED`.

## 10. Non-effects

This decision does not alter G03 rules or roles, does not change ACR-P0-002 events, does not create RBAC assignments, does not bind P5 to P1, does not claim live Oracle/Domain readiness, does not enable a Product mutation, and does not modify v6.360.
