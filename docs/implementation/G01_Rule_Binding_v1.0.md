# G01 Rule Binding v1.0

Status: P1 BINDING — DESIGN ARTIFACT
Project: A01-EIMS / VS-01 Observation → G01
RuleSetId: G01-INQ
RuleSetVersion: 1.0

## 1. Authority and reuse

This binding reuses the existing G01 review logic found in the project UX source (EIMS v6.227 / v6.360 derived artifact). It does not treat the prototype as recovered P0 production contract.

The source checklist contains five checks:
- complete
- traceable
- duplicate
- classification
- actionable

The frozen G01 contract removed the old completeness rule (R01). Therefore only the following four rules are bound here.

## 2. Rule catalog

| Canonical Rule | Reused Source Check | Source-derived minimum semantics |
|---|---|---|
| R02 ORIGIN_CONTEXT_TRACEABLE | traceable | originChannel must be present. For contextual origins FIELD_VISIT, BENCHMARK, AUDIT, PERFORMANCE_DATA, TECHNOLOGY_STUDY, REGULATORY, STRATEGY, MEETING, originDetail or evidence must be present. |
| R03 DUPLICATE_HISTORY_CHECKED | duplicate | Human review: prior records/archive are checked for a similar case. The source does not provide an automated duplicate algorithm. |
| R04 TYPE_UNIT_INITIAL_ROUTE_LOGICAL | classification | sourceType/type and unit must be present. Presence is a system signal; logical validity is confirmed by the reviewer. |
| R05 CASE_WORTHY | actionable | title must be present and description must contain at least 15 characters. |

## 3. Rule execution semantics

The existing source distinguishes system signals from human review:
- R02: system signal.
- R03: human review.
- R04: system signal plus human confirmation of logical validity.
- R05: system signal.

No new duplicate-detection algorithm, classification ontology, routing algorithm, or Case-scoring model is introduced by this binding.

Rule Definition, Rule Version, Rule Execution, Workflow Transition, Gate Evaluation and Decision remain distinct artifacts.

Rule execution is immutable; a rerun creates a new execution.

## 4. Canonical result mapping

The source logic above does not define a production mapping from its boolean/null signals to the project's canonical outcomes:
PASS, FAIL, WARNING, NOT_APPLICABLE, ERROR.

Therefore the exact mapping remains OPEN and is not invented in this binding.

In particular:
- R03's human-review signal null is not silently converted to PASS.
- Absence of evidence is not silently converted to FAIL unless a later approved contract binds that interpretation.
- ERROR is not inferred from ordinary rule failure.

## 5. Gate aggregation

The source prototype required all five historical checks for APPROVE. That historical UX behavior cannot be copied as the current production gate because R01 was removed from the frozen G01 rule set.

Therefore the aggregation predicate for the current four-rule set remains OPEN.

No G01_COMPLETE outcome is introduced as a new rule or gate enum here.

## 6. Explicit non-goals

This artifact does not define:
- G01 decision endpoint/API DTO
- Oracle schema, SQL, package or migration
- authorization or capability model
- assignment model
- decision snapshot schema
- audit schema
- idempotency key semantics
- event/outbox schema
- G01 state transitions
- HOLD
- ReasonCode catalog
- G02/G04 behavior

Those items remain governed by their existing frozen decisions/contracts or their own open P1 gates.

## 7. Acceptance criteria for this binding

AC-RULE-01: The canonical G01 rule set contains exactly R02, R03, R04 and R05; R01 is absent.

AC-RULE-02: Each canonical rule maps to an existing source check without adding a new business criterion.

AC-RULE-03: R03 remains human-review based; no automated duplicate algorithm is introduced.

AC-RULE-04: R04 separates presence of type/unit from reviewer confirmation of logical validity.

AC-RULE-05: The source-derived semantics do not silently define canonical result mapping or gate aggregation.

AC-RULE-06: RuleSet identity is G01-INQ / 1.0.

## 8. Source evidence

Primary reused source:
EIMS_v6.360_DERIVED_SAFE_UX_LOCAL_AI_v1.html, G01 explicit review checklist section (version marker EIMS v6.227).

Project reconciliation confirms the prototype is UX/reference evidence and does not constitute recovered P0 production rules.

## 9. Next gate

Next phase is G01 Test Contract.

Before TDD RED, existing repository tests and evaluator boundary must be checked for reusable coverage. No Oracle, SQL/DDL, or production implementation is authorized by this artifact.
