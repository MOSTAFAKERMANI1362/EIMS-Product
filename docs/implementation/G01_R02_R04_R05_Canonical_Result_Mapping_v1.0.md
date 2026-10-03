# G01 R02/R04/R05 Canonical Result Mapping v1.0

Status: P1 BINDING — APPROVED IMPLEMENTATION CONTRACT
Project: A01-EIMS / VS-01 Observation → G01
RuleSetId: G01-INQ
RuleSetVersion: 1.0

## 1. Scope

This binding closes only the canonical result mapping for the source-derived portions of R02, R04 and R05. It does not define G01 gate aggregation, APPROVE eligibility, API/endpoint behavior, Oracle schema, authorization, assignment, snapshot, idempotency, or R03 behavior.

## 2. R02 — ORIGIN_CONTEXT_TRACEABLE

Source-derived signal:
- originChannel is required.
- For contextual origins FIELD_VISIT, BENCHMARK, AUDIT, PERFORMANCE_DATA, TECHNOLOGY_STUDY, REGULATORY, STRATEGY, MEETING, originDetail or evidence is required.

Canonical mapping:
- Required originChannel present, and contextual origin has originDetail/evidence when required → PASS.
- originChannel missing → FAIL.
- Contextual origin missing both originDetail and evidence → FAIL.

No additional origin taxonomy or evidence rule is introduced.

## 3. R04 — TYPE_UNIT_INITIAL_ROUTE_LOGICAL

Source-derived signal:
- sourceType/type must be present.
- unit must be present.
- Logical validity is a reviewer responsibility and is not inferred from presence.

Canonical mapping for the bound presence signal:
- sourceType/type present and unit present → PASS.
- sourceType/type missing → FAIL.
- unit missing → FAIL.

The reviewer logical-validity result remains OPEN and is not silently mapped by this binding.

## 4. R05 — CASE_WORTHY

Source-derived signal:
- title must be present.
- description must contain at least 15 characters.

Canonical mapping:
- title present and description length >= 15 → PASS.
- title missing → FAIL.
- description shorter than 15 characters → FAIL.

No Case scoring or additional worthiness criterion is introduced.

## 5. Canonical outcome vocabulary

Only the following outcomes are used by this binding:
PASS and FAIL for the source-derived deterministic signals above.

WARNING, NOT_APPLICABLE and ERROR are not invented for these cases by this binding.

## 6. Deferred decisions

Still OPEN:
- R04 reviewer logical-validity input and mapping.
- G01 gate aggregation across R02-R05.
- G01_COMPLETE / G01_INCOMPLETE / G01_BLOCKED aggregation semantics.
- APPROVE eligibility derived from the gate.
- production G01 Decision Service/Endpoint.

## 7. Acceptance criteria

AC-MAP-01: R02 deterministic source semantics map only to PASS/FAIL as specified.
AC-MAP-02: R04 presence signal maps only to PASS/FAIL; reviewer logical validity remains OPEN.
AC-MAP-03: R05 deterministic source semantics map only to PASS/FAIL as specified.
AC-MAP-04: No R01 behavior is reintroduced.
AC-MAP-05: No G01 gate aggregation or Decision transition is introduced.
