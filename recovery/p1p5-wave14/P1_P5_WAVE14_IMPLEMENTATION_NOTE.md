# P1→P5 Wave14 — Knowledge Binding

Status: IMPLEMENTATION CANDIDATE — LOCAL VALIDATION REQUIRED

Wave14 extends the controlled P1→P5 user-command binding from 29 to 32 recovered mutations by binding the merged Wave13 Knowledge runtime.

## Binding contract
- version: `P1P5-1.3.0`
- historical/Portfolio/Execution/Benefit mutations remain available
- Knowledge adds exactly 3 user mutations:
  - `knowledge.create-draft`
  - `knowledge.validate`
  - `knowledge.publish`

## Authority boundary
- P3 server-resolved PersonID / exact Assignment / Role / Scope remains authoritative.
- client role/scope/ownership/author claims are never authority.
- create-draft requires server-side REALIZED Benefit evidence and author-policy evidence inside `KnowledgeServiceWave13`.
- Knowledge Steward validates; Knowledge Publisher publishes.
- Publication dossier remains domain-enforced.
- system/domain events are not user commands.
- Pilot Host remains `FailClosedCommandGateway` until physical P2/P3/OP-04 prerequisites are satisfied.

## Regression strategy
Historical Wave9/Wave12 mapping suites contain intentional global-version assertions for `P1P5-1.2.0`; they are superseded in this wave by the consolidated Wave14 mapping suite, which rechecks all 6 Portfolio, 9 Execution, 8 Benefit and 3 Knowledge commands against `P1P5-1.3.0`.

The local gate also runs:
- historical pre-Portfolio P1→P5 regression
- Wave12 real Execution/Benefit composition regression
- Wave10 Execution lifecycle + intake
- Wave11 Benefit lifecycle + intake
- Wave13 Knowledge runtime
- ACR-P0-008 verifier

Expected combined evidence: 177 checks.

## Non-claims
- no physical Oracle binding
- no live Domain/IIS/TLS activation
- no OP-04/Network Pilot readiness
- frozen v6.360 unchanged
