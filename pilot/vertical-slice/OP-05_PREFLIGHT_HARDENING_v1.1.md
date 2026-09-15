# OP-05 — Preflight Hardening v1.1

## Purpose

Strengthen the existing OP-05 vertical-slice plan before live Pilot execution, without changing product semantics, v6.360, or claiming environment readiness.

## Why v1.1 was needed

The v1.0 test matrix covered the major end-to-end controls but grouped several post-G04 invariants too broadly. P0 Wave 2 recovery later made the following distinctions explicit and they need their own PASS evidence:

- PortfolioIntakeCandidate != Portfolio Membership != Execution Handoff
- assignment alone does not create accepted membership
- accepted membership precedes active ExecutionRecommendation
- approved ExecutionRecommendation + approved baseline precede execution handoff
- 100% progress != Execution completion
- completion review is separate where required
- Execution COMPLETED != CLOSED
- Benefit measurement, verification, attribution/validation and realization remain distinct
- Benefit closure requires the required Knowledge asset to be published
- Knowledge validation != publication
- KNOWLEDGE_STEWARD != KNOWLEDGE_PUBLISHER

## Result

`op-05-test-matrix.v1.1.json` defines 28 mandatory LIVE evidence checks. The dependency-free .NET verifier locks the required test IDs and prevents silent weakening of the final gate.

## Non-goals

This change does not:
- activate Network Pilot;
- substitute LAB evidence for PILOT_ENVIRONMENT_EVIDENCE;
- bind Oracle, IIS, Windows Domain, TLS, HR or backup infrastructure;
- add or change business workflow semantics;
- modify frozen v6.360;
- allow test-only shortcuts to final PASS.

## Gate semantics

Preflight CI may PASS before OP-04 closes. Final OP-05 PASS may not.

Final execution still requires real authenticated and persistent environment evidence and `ALL_REQUIRED_PASS` across the 28 mandatory checks.
