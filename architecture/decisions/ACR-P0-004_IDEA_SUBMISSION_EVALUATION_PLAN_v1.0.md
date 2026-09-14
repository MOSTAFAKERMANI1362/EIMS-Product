# ACR-P0-004 — Idea Submission & Dynamic Evaluation Plan v1.0

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION

## Decision

`ideas.submit-g04` is stabilized as the server-authoritative transition from an Idea in `DRAFT` or `RETURNED` into formal evaluation.

The command may proceed only for a valid `IDEA_OWNER` assignment in the authoritative Idea scope, when the current Idea version matches the supplied `expectedVersion`, passport completion is at least 70%, an active strategy linkage exists, and the primary objective is ready.

A successful submission must atomically:

1. move the Idea to `UNDER_REVIEW` and increment the Idea version exactly once;
2. server-stamp the submission marker/time;
3. create one active Dynamic Evaluation Plan bound to the resulting Idea version;
4. create distinct evaluator assignments bound to Idea ID + Idea version + plan + role + scope;
5. persist Audit + Outbox + Idempotency in the same transaction.

The initial submission **does not create the G04 decision assessment**. That assessment is created only after all evaluation assignments marked required by the active Dynamic Evaluation Plan are complete.

## Evaluation Plan authority

The Dynamic Evaluation Plan is the source of truth for which evaluations are required.

Mandatory roles for the stabilized baseline are:

- `IDEA_EVALUATOR`
- `UNIT_OWNER_REVIEWER`

Technical, HSE, Financial and IT assessors are candidate optional roles. They become required only when activated by configuration/profile logic for the specific Idea; they must not be globally hard-coded as mandatory.

Evaluator work is assignment-bound. Role visibility alone is not authorization. Opening or completing evaluator work requires exact server validation of:

`AssignmentId + IdeaId + IdeaVersion + Role + Scope`

Direct, general, mismatched or stale links fail closed.

If the current Idea version changes, an active plan/assignment bound to an older Idea version becomes stale and must be superseded or invalidated. It must never be silently reused across Idea versions.

## Event decision

The post-freeze integration event for successful submission is:

`IdeaSubmittedForEvaluation.v1`

It is a **new post-freeze event identity**, not a recovered historical event name. It follows the approved `<Entity><PastTenseAction>.v<Major>` naming standard and is emitted from the Outbox only after the atomic transaction commits.

The event payload is deliberately minimized to Idea/version, Evaluation Plan identity/version, and required assignment IDs/roles. Idea passport, dossier, attachments, evaluation answers, detailed financial data and strategy document bodies are not duplicated into the event payload.

## Provenance boundary

The recovered P0 Wave 1 artifact contains a historical `G04Assessment` creation side effect on initial submission. Frozen product behavior and the stabilized evaluation workflow require the Dynamic Evaluation Plan and evaluator assignments to exist before the decision assessment is created.

This ACR does **not** rewrite or relabel that historical recovery artifact. It establishes the controlled post-freeze implementation authority for the submission/evaluation-plan boundary and explicitly records the distinction.

Frozen audit labels such as `DynamicIdeaEvaluationPlanCreated`, `IdeaOwnerSubmissionFrozenAndRoutedToEvaluators`, `IdeaSubmittedForAssessment` and `AssessmentCreated` are not promoted into integration event identities.

## Security and authority

The client may not choose or override state, version, Evaluation Plan identity, assignment IDs, assignment roles, required/optional classification, routing or integration event type.

Server authorization continues to require PersonID + effective AssignmentID + Role + Scope. Assignment scope must match the authoritative Idea scope.

Committee voting and final G04 decision remain separate downstream responsibilities:

- `g04.vote` → `G04_COMMITTEE_MEMBER`
- `g04.final-decision` → `IDEA_DECISION`

Submitting the Idea does not create either a committee vote or final decision.

## Runtime promotion gate

This ACR alone does not enable `ideas.submit-g04` in P1. Runtime promotion still requires an Idea submission rule evaluator, related-entity mutation planner, Evaluation Plan/Assignment persistence model, atomic related-entity transaction tests, assignment-context authorization, stale-version invalidation tests, event binding, server authorization tests and an explicit later P5 composition decision.

## Non-effects

This decision does not modify frozen `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`, does not implement G04 voting/final decision, does not bind P5, and does not claim live Oracle, Windows Domain or Network Pilot readiness.
