# ACR-P0-005 — Evaluation Assignment Completion and G04 Readiness

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` — unchanged.

## Purpose

This decision stabilizes the server-authoritative lifecycle for completing one evaluator mission created by the Dynamic Evaluation Plan. It does not implement the runtime by itself and it does not alter the frozen v6.360 executable specification.

The recovered Product command identity remains `evaluation-assignments.complete`. The recovered P0 role token `MATCH_ASSIGNMENT_ROLE` means the authenticated Person must possess a valid server-side authority assignment whose Role matches the exact evaluator mission Role; it does not mean that role visibility in the UI is authorization.

## Two different assignment identities

The implementation must keep these concepts separate:

- **authorityAssignmentId:** P3/RBAC assignment proving the authenticated Person currently holds the evaluator Role and Scope.
- **evaluationAssignmentId:** workflow mission created by the Dynamic Evaluation Plan for one exact Idea version.

Both are required for a completion mutation. Neither can substitute for the other.

## Completion authority and mission coherence

A completion request targets one exact `evaluationAssignmentId`. The server re-resolves the authenticated Person and `authorityAssignmentId`, verifies that the authority Role equals the evaluation Role, and verifies that authority Scope covers the mission/Idea Scope.

The evaluation assignment must be `PENDING`, its Dynamic Evaluation Plan must be `ACTIVE`, and the assignment must match the current exact `IdeaId + IdeaVersion + EvaluationPlanId`. Stale Idea versions and superseded or cancelled plans fail closed before any mutation.

## Role-specific assessment validation

The lifecycle envelope is generic, but assessment content is not. Every evaluator Role requires a versioned assessment schema and a server-bound validator identified by `evaluationRole + assessmentSchemaVersion`.

This ACR intentionally does **not** invent the role-specific question schemas. If the validator/schema for a Role is not bound, runtime completion must fail closed. A generic handler may not fabricate or silently accept missing Technical, HSE, Financial, IT, Unit Owner, or Idea Evaluator domain answers.

## Atomic completion

After successful role-specific validation, the server:

1. creates an immutable normalized assessment snapshot linked to the evaluator Person, authority assignment, evaluator mission, Idea version, schema version, timestamp and correlation;
2. transitions the evaluator mission `PENDING → COMPLETED` with one version increment;
3. recalculates Plan readiness from the Plan's authoritative required assignments;
4. records Audit, Outbox and Idempotency evidence in the same transaction.

Exact replay is idempotent and creates no duplicate evidence. A different completion fingerprint against the same already-completed mission is a conflict and cannot overwrite history.

## Plan readiness and G04 assessment creation

Only assignments activated by the Dynamic Evaluation Plan participate in readiness. Required assignments block readiness; optional role candidates that were never activated do not.

If required assignments remain incomplete, the Plan stays `ACTIVE` and no G04 Decision Assessment is created.

When the final required assignment completes, the same transaction:

- sets the Plan to `READY_FOR_G04_DECISION`;
- creates exactly one server-identified `G04Assessment` in `PENDING` state;
- binds it to the same Idea ID, exact Idea version and Evaluation Plan;
- freezes the Plan snapshot used for downstream G04 governance.

This action does not create a committee vote and does not create the final Idea decision. `g04.vote` and `g04.final-decision` remain separate downstream authorities.

## Post-freeze event contracts

Two explicit new event identities are approved for implementation:

- `EvaluationAssignmentCompleted.v1` — emitted on every successful evaluator completion.
- `G04DecisionAssessmentCreated.v1` — additionally emitted only when the final required completion creates the G04 assessment.

Both are Outbox events delivered at least once with EventId-based consumer idempotency. Their payloads contain identifiers, versions and statuses only. Assessment answers, narratives, attachments, financial details, technical details, HSE details and IT details are not duplicated into integration events.

## Transaction boundary

Every successful completion atomically persists:

`EvaluationAssignment State/Version + Immutable Assessment Snapshot + EvaluationPlan Readiness + Audit + Outbox + Idempotency`

When the final required assignment completes, `G04DecisionAssessment` is included in that same transaction. Any failure rolls back the complete unit of work.

## Security invariants

The client cannot authoritatively choose evaluator Person, authority assignment, evaluator Role/Scope, Idea version, Plan state, Assignment state, G04 Assessment identity, or Event type. The server must reauthorize every completion against both the RBAC assignment and the exact workflow mission context.

## Runtime promotion gate

ACR-P0-005 alone does not enable `evaluation-assignments.complete`. Wave 6 must still provide versioned role-specific assessment schema/validator bindings, assignment/assessment persistence, Plan readiness recalculation, one-time G04 assessment creation, multi-event Outbox support, atomic persistence tests, exact authority/mission context tests, stale-version tests and idempotency-conflict tests.

P5 remains fail-closed. No physical Oracle, live Windows Domain, or Network Pilot readiness is claimed by this decision.
