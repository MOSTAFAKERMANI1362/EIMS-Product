# EIMS Evaluator Assessment Schema Registry v1.0

**Recovery class:** `RECOVERED_FROM_FROZEN_V6_360_NOT_ORIGINAL_SERVER_SCHEMA`  
**Frozen source:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Purpose

ACR-P0-005 requires `evaluation-assignments.complete` to use a server-bound, versioned assessment schema/validator selected from the evaluator mission Role. The original historical server schema package is not available. This registry therefore recovers only the user-facing question keys, option values, score ranges and validation constraints that are evidenced in the frozen v6.360 executable product specification.

This artifact must not be represented as the original historical server schema. It does not alter v6.360 and it does not enable the evaluator-completion command by itself.

## Recovered roles

The registry contains exactly six G04 evaluator roles:

- `IDEA_EVALUATOR`
- `TECHNICAL_ASSESSOR`
- `UNIT_OWNER_REVIEWER`
- `HSE_ASSESSOR`
- `FINANCIAL_ASSESSOR`
- `IT_ASSESSOR`

Every mission is bound by the server to one Role and one schema version. The client may not substitute another Role schema or downgrade/replace the schema version.

## Important correction from the frozen baseline

The final v6.360 generic specialist answer values are:

- `CONFIRMED` — تأیید شد
- `CONDITIONAL` — مشروط / نیازمند اقدام
- `NOT_CONFIRMED` — تأیید نشد
- `MORE_EVIDENCE` — اطلاعات یا شاهد کافی نیست

Earlier intermediate interpretations such as YES/PARTIAL/NO/NA are not used in this recovery because they do not match the final frozen contract.

For Unit Owner, HSE, Financial and IT roles, every recovered question requires one of the four values above and a note of at least 3 characters. The overall evaluator outcome is one of `PASS`, `CONDITIONAL_PASS`, `RETURN`, `FAIL`; score is 1..5; evidence reference is required with minimum length 3; and the overall comment is required with minimum length 20.

Existing EIMS references to Need, Idea, Passport, reports or attachments may satisfy evidence-reference needs. The frozen UX does not establish a universal requirement to upload a new file for every assessment.

## Structured Idea evaluator

The structured evaluator has six weighted criteria whose weights total 100%:

- quality — 20%
- cost — 20%
- technical — 15%
- productionRisk — 15%
- timeReturn — 10%
- strategy — 20%

Every criterion score is 1..5. Criterion notes are stored by the prototype but are not universally required by the frozen save contract. Weighted score is derived decision-support information and is not a client-authoritative field.

Recommendation is required and uses exactly: `GO`, `REVISE`, `NO_GO`, `PILOT`, `TECH_REVIEW`. Final evaluator note is required with at least 10 characters.

## Technical assessor

The recovered technical criteria are:

`maturity`, `integration`, `infrastructure`, `maintainability`, `testability`, `vendor`, `skills`, `standards`.

Every criterion score is 1..5. Criterion notes are stored but not universally mandatory. Technical decision values are `PASS`, `CONDITIONAL_PASS`, `FAIL`, `MORE_EVIDENCE`. Pilot recommendation values are `YES`, `NO`, `CONDITIONAL`. Evidence reference and summary are required. Conditions become required only when the decision is `CONDITIONAL_PASS`.

The prototype also contained assessor-name and unit text fields. Those are deliberately **not** recovered as trusted production payload fields. Under ACR-P0-005, evaluator Person, authority assignment, Role and Scope are derived server-side from authenticated identity and the exact evaluator mission.

## Workflow completion is not a positive outcome

Frozen evaluator history stores workflow assignment status (`COMPLETED`) separately from the assessment outcome. The production contract preserves that distinction. A validated assessment may complete its workflow mission even when its professional outcome is negative or requests more evidence. Downstream G04 readiness/decision logic consumes the assessment outcome separately; completion itself must not silently convert `FAIL`, `RETURN`, `NOT_CONFIRMED`, or `MORE_EVIDENCE` into PASS.

This distinction is particularly important for the technical prototype, whose historical helper treated `PASS` and `CONDITIONAL_PASS` as sufficient for the G04 technical hard condition. That historical readiness rule is not reinterpreted as the definition of workflow completion.

## Server-authority boundary

The following are server-derived and are not assessment-form authority inputs:

- evaluator PersonID
- `authorityAssignmentId`
- `evaluationAssignmentId`
- evaluator Role
- evaluator Scope
- Idea ID/version and Evaluation Plan ID linked to the mission
- schema ID/version bound to the mission
- completion timestamp and authoritative workflow state

The assessment payload supplies only the fields defined by the bound Role schema. Missing schema/validator, wrong Role schema, or schema substitution must fail closed.

## Non-effects

This recovery does not modify v6.360, grant roles/scopes, bind P5, connect Oracle/Windows Domain, or claim Network Pilot readiness. P1 Wave 6 must still implement executable validators, mission-context authorization, immutable assessment snapshots, plan-readiness recalculation, one-time G04Assessment creation, multi-event Outbox and atomic persistence before `evaluation-assignments.complete` can be promoted.
