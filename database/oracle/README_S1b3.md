# EIMS S1b-3 — Waves 9–13

## Scope

This migration extracts the downstream workflow from the prototype into relational persistence:

1. Portfolio Candidate
2. Portfolio Membership
3. Execution Recommendation
4. Execution
5. Benefit
6. Knowledge Asset

The dependency chain is:

`Portfolio Candidate -> Membership -> Recommendation -> Execution -> Benefit -> Knowledge`

## Prototype-derived invariants

- Portfolio eligibility creates a candidate from an approved Idea Version and records eligibility dimensions/rule reference.
- A Portfolio Membership belongs to a Portfolio Candidate.
- An Execution Recommendation belongs to a membership and portfolio.
- Approved Baseline is required before execution handoff.
- Execution is created from an accepted handoff/recommendation.
- Benefit follows the prototype's ordered state machine:
  `OBLIGATION_PENDING_ACCEPTANCE -> BASELINE_REQUIRED -> PLAN_REQUIRED -> MEASUREMENT_PENDING -> MEASURED -> VERIFIED -> VALIDATED -> REALIZED -> CLOSED`.
- Knowledge is tied to a Benefit; publication/continuity is required before Benefit closure.

## Verification

Run as `EIMS_OWNER` inside `FREEPDB1`:

```sql
@migrations/V004__portfolio_execution_benefit_knowledge.sql
@verify/verify_waves_9_13.sql
```

Do not run the migration twice. If verification fails, diagnose the specific constraint/test before resetting anything.
