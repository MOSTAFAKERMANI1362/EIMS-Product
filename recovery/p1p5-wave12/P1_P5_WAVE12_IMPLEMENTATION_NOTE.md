# P1→P5 Wave12 — Execution + Benefit Binding

Status: IMPLEMENTATION CANDIDATE — NOT PILOT ACTIVATION

Wave12 extends the controlled P1→P5 user-command binding from the Wave9 Portfolio-era 12 recovered mutations to all 29 mutations recovered through Wave11.

## Bound user mutations

- Historical pre-Portfolio: 6
- Portfolio: 6
- Execution: 9
- Benefit: 8

## Critical boundaries

- P3 server-resolved PersonID / AssignmentID / Role / Scope remains authoritative.
- Client role, scope grants and ownership claims are not authority.
- Execution binding uses `ExecutionServiceWave10Guarded`; the inner Execution service is not a P5 production binding target.
- Benefit binding uses `BenefitServiceWave11`, which resolves Benefit ownership and specialist evidence server-side.
- `ExecutionCreatedFromRecommendation.v1` and `BenefitObligationCreated.v1` remain system-event intake paths and are not HTTP/user commands.
- `executions.prepare` remains fail-closed.
- If-Match expected version, Idempotency-Key and correlation ID remain mandatory.
- The Pilot Host continues to register `FailClosedCommandGateway` until physical P2/P3/OP-04 prerequisites are available.

## Validation target

Local Windows/.NET 10.0.302 release gate runs:

- Pilot Host build
- historical P1→P5 regression
- Wave9 Portfolio binding regression
- Wave12 Execution+Benefit binding tests
- Wave10 Execution lifecycle + intake regressions
- Wave11 Benefit lifecycle + intake regressions
- ACR-P0-008 verifier

No Network Pilot readiness is claimed by this wave.
