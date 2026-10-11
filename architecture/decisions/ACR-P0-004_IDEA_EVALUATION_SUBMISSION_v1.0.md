# ACR-P0-004 — Idea Submission / Dynamic Evaluation Plan

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Why this ACR exists

The frozen product and P0 Product Contract clearly establish the business behavior around Idea-owner submission and creation of a Dynamic Evaluation Plan, but the historical P0 machine recovery did not recover a complete authoritative transition/event contract for `ideas.submit-g04`. We therefore stabilize the behavior as a controlled post-freeze decision rather than silently presenting it as historical recovery.

Audit action names in the HTML such as `DynamicIdeaEvaluationPlanCreated`, `IdeaOwnerSubmissionFrozenAndRoutedToEvaluators`, and `EvaluationRoutingRecovered` remain audit/action evidence. They are not promoted as integration Domain Event identities.

## Command contract

`ideas.submit-g04` is an Idea-owner command. A valid server-side `IDEA_OWNER` assignment and scope are required. The only allowed source states are `DRAFT` and `RETURNED`.

Before mutation the server must verify:

- `expectedVersion` equals the current Idea version;
- Idea passport completion is at least 70%;
- an active strategy linkage exists;
- the primary strategy objective is resolved;
- idempotency key and authenticated assignment context are present.

On successful submission, in one logical atomic transaction:

1. Idea state becomes `UNDER_REVIEW`.
2. Idea version increments exactly once.
3. `specialistReviewRequested=true` and its timestamp are server-stamped.
4. one active Dynamic Evaluation Plan is created for the resulting Idea version.
5. evaluator assignments are created with server-generated distinct IDs.
6. Audit, Outbox and Idempotency evidence are committed with the same transaction.

The initial command does **not** create the G04 decision assessment.

## Dynamic Evaluation Plan authority

The Dynamic Evaluation Plan is the source of truth for the required G04 evaluations. Two assignments are mandatory in the baseline:

- `IDEA_EVALUATOR`
- `UNIT_OWNER_REVIEWER`

Other specialist roles are conditional and must be selected through approved configuration/profile rules rather than globally hard-coded:

- `TECHNICAL_ASSESSOR`
- `HSE_ASSESSOR`
- `FINANCIAL_ASSESSOR`
- `IT_ASSESSOR`

A zero estimated cost alone is not sufficient to suppress a financial assessment. Financial need remains based on the configured multi-signal evaluation-plan logic.

## Assignment-bound work

An evaluator form is not an authorization boundary by itself. The server must validate the exact assignment context:

`AssignmentId + IdeaId + IdeaVersion + Role + Scope`

A direct/general link, stale assignment, wrong role, wrong PersonID/scope, or version mismatch must fail closed. UI role visibility remains UX only.

## Version consistency

The Evaluation Plan must be bound to the resulting Idea version after submission. If the Idea version later changes materially, the old plan cannot silently accept new completion evidence. The plan/assignments must be superseded or repaired through a controlled process while preserving historical evidence.

## G04 handoff

Only after every required assignment for the current Idea version is complete may the downstream handoff create a G04 decision assessment. That assessment is `PENDING`, is routed to `IDEA_DECISION`, and must preserve the current Idea version and Evaluation Plan snapshot. Committee voting and final decision authority remain separate concerns.

## Domain event decision

This ACR introduces the explicit post-freeze Domain Event:

`IdeaSubmittedForEvaluation.v1`

This name follows the approved `<Entity><PastTenseAction>.v<Major>` pattern. It is **not** claimed to be a recovered historical event name.

The event is emitted through the Outbox only after the atomic commit. Its payload is intentionally small: Idea ID/version, Evaluation Plan ID/version, and the required assignment references/roles. The full Idea passport, full Need dossier, attachment content, financial narrative and personal notes must not be copied into this event.

## Transaction boundary for Wave 5

Wave 5 must use a related-entity transaction model. The Evaluation Plan and its Assignments are first-class related records; they must not be flattened into `RuleFacts` merely to avoid a proper persistence model.

The required logical transaction is:

`Idea State/Version + Dynamic Evaluation Plan + Evaluation Assignments + Audit + Outbox + Idempotency`

Partial commit is prohibited.

## Security boundary

All authoritative values are server-computed. A client cannot choose the resulting state/version, Plan ID, Assignment IDs, required/optional flags, assignment roles outside the server plan rules, or Domain Event identity.

## Non-effects

This decision does not:

- enable `ideas.submit-g04` by itself;
- implement evaluator completion;
- implement G04 voting or final decision;
- bind the P5 command gateway;
- claim physical Oracle readiness;
- claim live Windows Domain readiness;
- claim Network Pilot readiness;
- modify frozen v6.360.

A separate P1 Recovery Wave 5 is required to implement the related-entity transaction model and promote the command only after all logical tests pass.
