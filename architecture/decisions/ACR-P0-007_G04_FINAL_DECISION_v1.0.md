# ACR-P0-007 — G04 Final Decision Authority, Evidence Freeze and Outcome Semantics

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen Product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` — unchanged

## Purpose

This ACR stabilizes the server contract for `g04.final-decision` before runtime promotion. It does not itself enable the command.

The final authority is `IDEA_DECISION`, separate from `G04_COMMITTEE_MEMBER`. P3 authenticated PersonID, the exact authority Assignment and Idea Scope are server-derived and rechecked. A frozen committee member cannot act as final decider for the same assessment.

## Exact final outcomes

| Outcome | Idea state | Business Idea revision | Domain event |
| --- | --- | ---: | --- |
| `APPROVE` | `APPROVED` | unchanged | `IdeaApprovedForPortfolio.v1` |
| `RETURN` | `RETURNED` | +1 | `IdeaReturnedFromG04.v1` |
| `HOLD` | `HOLD` | unchanged | `IdeaHeldAtG04.v1` |
| `REJECT` | `REJECTED` | unchanged | `IdeaRejectedAtG04.v1` |

The frozen executable uses Idea version as a business/content revision and increments it on RETURN. Production additionally maintains a **separate technical concurrency/state-mutation version** that increments by exactly one on every successful final-decision mutation. This preserves frozen business semantics without weakening optimistic concurrency.

## Required final-decision input

- outcome: exactly `APPROVE`, `RETURN`, `HOLD`, or `REJECT`;
- decision comment: trimmed minimum 15 characters;
- reason code: one of `G04_READY`, `G04_SOLUTION_INCOMPLETE`, `G04_EVIDENCE_INCOMPLETE`, `G04_COST_INCOMPLETE`, `G04_RISK_UNRESOLVED`, `G04_TECH_FAIL`, `G04_ALIGNMENT`, `OTHER`;
- `APPROVE` requires `G04_READY`;
- `RETURN`, `HOLD`, and `REJECT` must not use `G04_READY`;
- `HOLD` additionally requires review date.

Reason code and free-text rationale are authoritative decision evidence. Free-text rationale is not copied into integration events.

## Authoritative decision route — route integrity hardening

A recovery gap was identified after Wave 7: the current recovered `EvaluationPlanEnvelope` and `G04AssessmentEnvelope` do not persist the authoritative G04 decision route. The frozen executable, however, explicitly distinguishes the committee route from the individual-decision route and shows both the decision route and decision method in final-decision context.

Production must therefore persist server-derived route context in both the Evaluation Plan and the G04 Assessment. New runtime objects require:

- `decisionRoute`;
- `decisionRouteKind` = `COMMITTEE` or `INDIVIDUAL`;
- `decisionMethod`;
- `governanceProfileId` and `governanceProfileVersion`.

The route is computed by the server when the Evaluation Plan is created and is copied/frozen into the G04 Assessment when readiness is reached. Assessment values must exactly match the Plan snapshot. Missing, unknown, stale, or mismatched route context fails closed.

`G04_COMMITTEE` is the committee route. The individual route identifier may be configuration-driven; `UNIT_RND_DECISION` is the frozen baseline example and is not a universal customer hard-code. Committee decision method must agree with the frozen vote rule (`MAJORITY`, `CONSENSUS`, or `CHAIR_TIEBREAK`). Individual decision method is `INDIVIDUAL_GOVERNANCE_DECISION`.

This closes a concrete runtime safety gap: `g04.vote` may execute **only** when the authoritative Assessment route is `G04_COMMITTEE`. An individual-route Assessment must reject committee voting, even if a stale or malicious committee snapshot exists. Client-supplied route/method/governance values are never authority.

The route hardening is machine-defined in `ACR-P0-007_ROUTE_INTEGRITY_ADDENDUM_v1.0.json`. Historical recovery artifacts remain unchanged; only new production/recovery runtime objects are required to carry the route fields.

## APPROVE-only rule suite

Positive G04 gates block only `APPROVE`. Authorized `RETURN`, `HOLD`, and `REJECT` remain available when positive approval criteria fail.

For APPROVE, the server must evaluate the exact frozen/current decision context: profile snapshot, score threshold, data readiness, base Need, conditional technical requirement, configured hard conditions, required specialist outcomes, committee positive result when committee route applies, and SoD.

### Specialist outcome vocabulary

New production authority uses exactly:

- `CONFIRMED`
- `CONDITIONAL`
- `NOT_CONFIRMED`
- `MORE_EVIDENCE`

For APPROVE, `CONFIRMED` and `CONDITIONAL` are acceptable; `NOT_CONFIRMED` and `MORE_EVIDENCE` block approval.

Legacy prototype values such as `PASS`, `CONDITIONAL_PASS`, `RETURN`, `FAIL`, `NO_GO`, `APPROVE`, and `APPROVED` are **read-compatibility aliases only**. They must be normalized before evaluation and must never be emitted by new runtime code.

## Committee-route finalization

When `decisionRoute == G04_COMMITTEE`, the committee voting stage must be `COMPLETED` against the exact frozen committee snapshot/version **before any final outcome**.

For `APPROVE`, stage completion is not enough: the frozen committee result must also have `approvalRuleSatisfied=true`.

For `RETURN`, `HOLD`, or `REJECT`, a completed non-approving committee result is a valid basis for final resolution. This matches the frozen workflow where committee work ends first and the separate final authority then resolves the case.

For an `INDIVIDUAL` route, committee state is not required and must not be synthesized merely to satisfy the final-decision service.

## Evidence freeze and history

The final decision freezes profile/rule evidence at the decision instant. Closed decisions may not be reconstructed from a later active profile. The append-only decision record stores authority, reason, business revision before/after, technical mutation version before/after, profile/rule snapshot references, specialist summary, authoritative route/method/governance reference, committee-stage evidence where applicable, SoD result, timestamp and correlation.

RETURN supersedes old active Evaluation Plans and old pending G04 assessments for the prior Idea revision and makes prior technical/structured evaluation evidence stale for the new revision.

## Transaction and events

One logical transaction covers:

`Idea state/business revision + technical state-mutation version + G04Assessment closure + immutable final-decision evidence + RETURN supersession metadata when applicable + Audit + outcome Outbox + Idempotency`.

Portfolio eligibility is not part of this transaction. `IdeaApprovedForPortfolio.v1` is the post-commit boundary for idempotent `PortfolioEligibilityService` execution. A downstream Portfolio failure must not roll back an already committed G04 decision.

Integration events contain identifiers, status/version and decision-evidence references only. They exclude decision comment, evaluator answers, full Idea dossier, full profile snapshot, full committee membership snapshot and vote notes.

## Safety boundaries

This ACR does not promote `g04.final-decision`, does not bind P5, does not choose physical Oracle details, does not claim live Windows Domain or Network Pilot readiness, and does not modify v6.360.
