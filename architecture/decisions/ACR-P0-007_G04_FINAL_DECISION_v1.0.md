# ACR-P0-007 — G04 Final Decision Authority, Evidence Freeze and Outcome Semantics

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen Product reference:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## 1. Purpose

This ACR stabilizes the production contract for `g04.final-decision`. It does not promote that command into runtime. The historical complete P0 server rule package is not available; therefore this decision combines frozen v6.360 executable evidence with explicit post-freeze architecture decisions and labels them accordingly.

The required recovered machine evidence is `recovery/g04-decision/G04_DECISION_PROFILE_RULE_REGISTRY_v1.0.json`, classified as `RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA`.

## 2. Authority boundary

Only an authenticated P3 `AuthorityActor` with exactly one effective `IDEA_DECISION` assignment may execute the final decision. PersonID, authority AssignmentId, role and scope are server-derived. The client may not supply or override them.

A person who is part of the frozen G04 committee for the same assessment may not act as final decision authority. Committee voting and the final G04 authority remain separate.

Evaluator/final-decider conflict is checked for `APPROVE`. Default policy is deny. A frozen governance policy may explicitly permit `ALLOW_WITH_AUDIT`; that exception must be captured in immutable decision evidence and audit. Client input cannot create the exception.

## 3. Exact decision context

The target G04Assessment must be `PENDING` and linked to the exact current Idea version and exact current Evaluation Plan. Superseded/stale plans or assessments are rejected. Optimistic concurrency and an idempotency key are mandatory.

Open G04 uses the current active scoring profile as the working source of truth. At final decision, the profile/rule evidence used for the decision becomes immutable. Historical decisions must never be reconstructed later from whatever profile is active at that future time.

## 4. Outcomes

The only final outcomes are `APPROVE`, `RETURN`, `HOLD`, and `REJECT`. A trimmed decision explanation of at least 15 characters is required for every outcome. `HOLD` additionally requires a review date.

| Outcome | Idea result | Version | Assessment | Event |
|---|---|---:|---|---|
| APPROVE | `APPROVED` | unchanged | `DECIDED` | `IdeaApprovedForPortfolio.v1` |
| RETURN | `RETURNED` | +1 | `DECIDED` | `IdeaReturnedFromG04.v1` |
| HOLD | `HOLD` | unchanged | `DECIDED` | `IdeaHeldAtG04.v1` |
| REJECT | `REJECTED` | unchanged | `DECIDED` | `IdeaRejectedAtG04.v1` |

`IdeaApprovedForPortfolio.v1` is an existing frozen event identity. The RETURN/HOLD/REJECT event identities above are post-freeze stabilized identities for production implementation.

## 5. Approval-only rule suite

The positive gate suite blocks only `APPROVE`. It does not prevent an authorized `RETURN`, `HOLD`, or `REJECT`; those outcomes still require exact authority/context, valid rationale, concurrency, idempotency and immutable evidence.

For `APPROVE`, the server must prove all of the following:

1. Exact frozen profile/rule evidence exists and is usable.
2. Calculated gate score is at least the frozen profile `passThreshold`.
3. Data readiness is at least the frozen profile `dataThreshold`.
4. The base Need exists.
5. Technical assessment is valid only when required by the current Evaluation Plan or by frozen profile hard keys `TECHNICAL_ASSESSMENT_VALID` / `HSE_AND_TECH_VALID`. There is no unconditional technical gate.
6. Every profile hard condition maps to a known machine rule and passes. Unknown mapping fails closed.
7. Required specialist assignments contain no blocking `FAIL`, `RETURN`, or `NO_GO`; HSE `FAIL`/`RETURN` is blocking.
8. If route is `G04_COMMITTEE`, Wave 7 committee evidence must show `COMPLETED` and `ApprovalRuleSatisfied=true` against the exact frozen committee snapshot/version.
9. Separation of Duties passes, or the evaluator conflict is covered by a frozen `ALLOW_WITH_AUDIT` exception. Committee-member/final-authority overlap is never accepted for the same assessment.

## 6. Recovered profile/rule model

The recovered registry contains exactly two baseline profile contracts: `G04-GENERAL-V1.0` and `G04-HSE-V1.0`, their pass/data thresholds, nine scoring weights, the frozen score formula, the 12-item data-readiness calculation, six machine hard-rule keys, specialist blocking outcomes and the conditional technical requirement.

The six known hard-rule keys are:
`REQUIRED_EVALUATIONS_COMPLETE`, `BASE_NEED_G03_READY`, `TECHNICAL_ASSESSMENT_VALID`, `ACTIVE_STRATEGY_ALIGNED`, `HSE_ASSESSMENT_ACCEPTABLE`, `HSE_AND_TECH_VALID`.

Unknown hard-condition text is a fail-closed condition for `APPROVE`.

## 7. Decision evidence freeze

The final decision writes an append-only immutable decision record containing, at minimum: decision/assessment/Idea identifiers and versions; outcome and rationale; HOLD review date when relevant; authenticated PersonID/network identity/authority AssignmentId; profile/rule identifiers and profile-snapshot hash; gate/data scores; machine rule results; specialist outcome summary; committee snapshot/version/approval result when applicable; SoD result/exception; timestamp and correlation ID.

The historical decision record is never overwritten.

## 8. RETURN invalidation

`RETURN` increments the Idea version exactly once. That version change supersedes old active Evaluation Plans and old PENDING G04 assessments and marks old technical/structured evaluation evidence stale. The new Idea version must obtain a fresh evaluation context before another final G04 approval attempt.

## 9. Atomicity and events

One logical transaction contains:

`Idea state/version + G04Assessment final closure + immutable final-decision evidence + RETURN supersession metadata (when applicable) + Audit + outcome Outbox + Idempotency`.

Integration event payloads are minimized. They may contain identifiers, versions, statuses and decision-evidence references. They must not duplicate free-text rationale, evaluator answers, full Idea dossier, full profile snapshot, full committee membership snapshot or vote notes.

## 10. Portfolio boundary

The final G04 transaction never creates a `PortfolioIntakeCandidate`. `APPROVE` commits first and emits `IdeaApprovedForPortfolio.v1`. An idempotent downstream `PortfolioEligibilityService` consumes that event after commit. A downstream eligibility failure may be retried or surfaced as a visible eligibility failure, but it must not roll back an already committed G04 governance decision.

## 11. Non-effects

This ACR does not modify v6.360, does not itself enable `g04.final-decision`, does not enable P5, does not physically bind Oracle or Windows Domain, and makes no Network Pilot READY claim.
