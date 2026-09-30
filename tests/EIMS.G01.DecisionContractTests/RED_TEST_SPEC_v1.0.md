# G01 Decision Snapshot & Decision Idempotency — RED Test Specification v1.0

Status: RED / implementation intentionally absent.

## Test IDs
- G01-SNAPSHOT-RED-01 — APPROVE snapshot completeness
- G01-SNAPSHOT-RED-02 — RETURN captures required ReasonCode
- G01-SNAPSHOT-RED-03 — REJECT captures required ReasonCode
- G01-SNAPSHOT-RED-04 — Snapshot is immutable
- G01-IDEMP-RED-01 — same key + same fingerprint replays one committed Decision
- G01-IDEMP-RED-02 — same key + different fingerprint conflicts without mutation
- G01-IDEMP-RED-03 — concurrent same-key requests produce one logical commit
- G01-IDEMP-RED-04 — failed transaction leaves no successful idempotency outcome
- G01-IDEMP-RED-05 — Submission and Decision Idempotency remain independent
- G01-SNAPSHOT-RED-05 — Snapshot binds the evaluated Observation business version

## Assertion rule
Tests must assert committed/persisted state and command results, not method-call choreography.

## RED gate
Current implementation does not yet expose the authoritative Snapshot + Decision Idempotency behavior above. These tests are expected to fail until GREEN implementation exists. Tests must not be weakened to fit the current implementation.
