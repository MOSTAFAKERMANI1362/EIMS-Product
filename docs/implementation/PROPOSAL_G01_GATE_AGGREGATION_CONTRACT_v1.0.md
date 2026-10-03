# PROPOSAL — G01 Gate Aggregation Contract v1.0

Status: PROPOSAL — NOT APPROVED / NOT FROZEN
Project Path: A01-EIMS → VS-01 → Observation → G01
Work Package: WP-G01-CLOSE
Purpose: resolve the single semantic gap preventing authoritative G01 Decision-path implementation.

## 1. Source-supported constraints

The current approved G01 contract establishes:

- RuleSetId = G01-INQ
- RuleSetVersion = 1.0
- Current rules = R02, R03, R04, R05
- Outcomes = APPROVE, RETURN, REJECT
- HOLD is removed.
- APPROVE requires G01_COMPLETE and no blocking FAIL/ERROR.
- RETURN/REJECT require ReasonCode.
- Decision Snapshot must contain all four rule results.
- NOT_APPLICABLE must not be silently converted to PASS.
- ERROR must not be silently converted to FAIL.

The sources do NOT explicitly define the complete aggregation truth table for
G01_COMPLETE / G01_INCOMPLETE / G01_BLOCKED.


## 1A. Confirmed G01 Decision Outcome

Project-owner clarification confirms that the G01 Decision has exactly three outcomes:

- APPROVE — تأیید
- RETURN — بازگشت جهت اصلاحات
- REJECT — رد

HOLD is not a G01 outcome. This clarification is consistent with the approved G01 contract and does not introduce an additional Decision outcome.

The aggregation values G01_COMPLETE / G01_INCOMPLETE / G01_BLOCKED are gate-evaluation states, not additional Decision outcomes.

## 2. Proposal

Bind G01 aggregation as a pure deterministic gate function over the four
immutable rule execution results.

Proposed precedence:

1. Missing/invalid required rule execution input → G01_BLOCKED.
2. Any ERROR → G01_BLOCKED.
3. Any FAIL → G01_INCOMPLETE.
4. Any WARNING or NOT_APPLICABLE → G01_INCOMPLETE.
5. All four rules PASS → G01_COMPLETE.

Rationale:
- preserves the approved distinction between ERROR and FAIL;
- avoids converting NOT_APPLICABLE into PASS;
- requires all four current rules to participate in the decision snapshot;
- makes G01_COMPLETE explicit rather than deriving it from a partial rule set.

## 3. Proposed APPROVE eligibility

APPROVE may proceed only when:

- aggregation == G01_COMPLETE;
- authorization/assignment/state/transition/SoD checks pass;
- the decision outcome is APPROVE;
- the evaluated Observation version is the version bound to the command.

RETURN and REJECT do not require G01_COMPLETE, but retain their approved
ReasonCode requirement and must use the authoritative Decision path.

## 4. Proposed Decision-path acceptance

The authoritative G01 Decision path should compose:

Authenticated actor
→ G01 assignment/authorization
→ R02/R03/R04/R05 evaluation
→ aggregation
→ APPROVE/RETURN/REJECT eligibility
→ immutable Decision
→ immutable Snapshot
→ Audit
→ Decision Idempotency
→ required domain mutation/event staging.

No Oracle/SQL/DDL change is part of this proposal.

## 5. Proposed TDD gate

If this proposal is explicitly approved:

RED:
- aggregation truth-table tests;
- APPROVE blocked unless aggregation is G01_COMPLETE;
- RETURN/REJECT ReasonCode behavior remains covered by existing tests;
- composed Decision-path tests for the approved transition/event behavior.

GREEN:
- minimal aggregation implementation;
- composed authoritative Decision path.

Regression:
- existing P2, Snapshot, Idempotency, R02-R05 and Assignment/Authorization suites.

## 6. Explicit non-goals

This proposal does not:
- reopen R02/R03/R04/R05 criteria;
- change RuleSet identity/version;
- introduce HOLD;
- redesign Snapshot or Idempotency;
- define Oracle schema or SQL;
- define API DTOs;
- change G02-G08 behavior.

## 7. Approval gate

No implementation or new behavioral tests based on the proposed truth table
should be treated as frozen until the project owner explicitly approves
this Proposal or supplies a different authoritative aggregation contract.
