# G01 Test Contract v1.0

Status: P1 TEST CONTRACT — DESIGN ONLY
Project: A01-EIMS / VS-01 Observation → G01
RuleSetId: G01-INQ
RuleSetVersion: 1.0

## 1. Basis

This test contract is derived only from:
- G01 Rule Binding v1.0
- existing G01 source checklist in EIMS v6.227 / v6.360 derived UX
- existing AuthorityKernel / PassRuleEvaluator boundary
- existing G01 ReasonCode and Snapshot contract tests

No new production business rule is introduced here.

## 2. Reuse check

Existing tests already cover:
- G01 ReasonCode validation: 5 cases
- G01 decision idempotency: 5 cases
- G01 Decision Snapshot: 5 cases
- AuthorityKernel rule-evaluation boundary
- persistence atomicity, concurrency and idempotency

Therefore these are not duplicated.

## 3. Rule-level test matrix

### R02 — ORIGIN_CONTEXT_TRACEABLE

Source-supported cases:
- R02-T01: originChannel present and non-contextual -> source signal true.
- R02-T02: contextual origin with originDetail/evidence present -> source signal true.
- R02-T03: contextual origin with neither originDetail nor evidence -> source signal false.
- R02-T04: missing originChannel -> source signal false.

Status: TESTABLE at source-semantics level.

### R03 — DUPLICATE_HISTORY_CHECKED

Source-supported behavior:
- human review of prior records/archive is required.
- no automated duplicate algorithm exists in the source.

Therefore:
- R03-T01 must verify that the rule is represented as a human-review input/evidence, not as an invented automatic duplicate detector.

Status: BLOCKED for executable production test until the production input shape and canonical result mapping for human review are bound.

### R04 — TYPE_UNIT_INITIAL_ROUTE_LOGICAL

Source-supported cases:
- R04-T01: sourceType/type and unit present -> system presence signal true.
- R04-T02: sourceType/type missing -> system presence signal false.
- R04-T03: unit missing -> system presence signal false.

The source additionally requires reviewer confirmation that type/unit are logically valid.

Status: Presence signal is testable; final composite production result is BLOCKED until reviewer-input and canonical result mapping are bound.

### R05 — CASE_WORTHY

Source-supported cases:
- R05-T01: title present and description length >= 15 -> source signal true.
- R05-T02: title missing -> source signal false.
- R05-T03: description shorter than 15 characters -> source signal false.

Status: TESTABLE at source-semantics level.

## 4. Deliberately deferred assertions

The following must NOT be asserted yet:
- source boolean/null -> PASS/FAIL/WARNING/NOT_APPLICABLE/ERROR mapping
- G01_COMPLETE/G01_INCOMPLETE aggregation
- APPROVE eligibility derived from R02-R05
- R03 automatic no-duplicate result
- R04 automatic logical-validity result
- G01 decision transition behavior

These are outside the approved Rule Binding because their production semantics remain OPEN.

## 5. RED gate

Executable RED tests may be added only for cases whose expected behavior is already contractually determined.

Current safe RED candidates:
- R02 source-semantic signal cases
- R04 presence signal cases
- R05 source-semantic signal cases

R03 executable production testing and composite R04 testing remain blocked pending the missing production input/result mapping.

## 6. Runner policy

No CI run is required for this design artifact.
If RED tests are later added, use one consolidated test-only commit and allow the existing PR workflow to run once. No manual retry or duplicate workflow.

## 7. Exit criteria

Before GREEN implementation:
1. R02/R04/R05 RED tests fail against the current production boundary for the intended missing behavior.
2. R03 production representation is explicitly bound.
3. Canonical result mapping is explicitly bound.
4. Gate aggregation is explicitly bound.

Only then should production rule evaluation be implemented.
