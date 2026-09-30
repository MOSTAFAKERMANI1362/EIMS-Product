# G01 Decision Snapshot & Decision Idempotency Contract Binding v1.0

Status: PROPOSED CONTRACT BINDING — implementation not yet authorized.
Scope: VS-01 / G01 only.

## Frozen prerequisites
- RuleSetId = `G01-INQ`, RuleSetVersion = `1.0`.
- G01 outcomes = APPROVE / RETURN / REJECT. HOLD is removed.
- APPROVE requires G01_COMPLETE, no blocking FAIL/ERROR, valid authorization, assignment, state, transition and SoD.
- RETURN/REJECT require non-empty ReasonCode.
- Submission Idempotency is distinct from Decision Idempotency.
- APPROVE maps to Case UNDER_REVIEW and CaseCreatedFromApprovedSource.v1.

## Decision Snapshot
The Snapshot is an immutable decision-time representation of the inputs/evaluations materially supporting the decision. It is not a UI snapshot and not a full copy of Observation.

Required semantic content:
- SnapshotId, DecisionId, ObservationId
- evaluated Observation business version
- RuleSetId + RuleSetVersion
- GateOutcome
- immutable R02/R03/R04/R05 execution results
- authorization evidence required to explain authorization, without credentials/tokens/raw claims
- DecisionOutcome
- ReasonCode when RETURN/REJECT
- command-level Comment where applicable, separate from ReasonCode
- CreatedAt
- Snapshot SchemaVersion
- deterministic integrity Fingerprint

Invariants:
1. Snapshot is immutable after commit.
2. It binds the evaluated Observation version and does not follow later versions implicitly.
3. It records RuleSet identity/version and all four G01 rule results.
4. NOT_APPLICABLE is not converted to PASS; ERROR is not converted to FAIL.
5. No secrets, credentials or authentication tokens are stored.
6. Decision, Snapshot, Audit, Idempotency outcome and required domain mutation/event staging share one production transaction boundary.

## Decision Idempotency
Applies only to the G01 Decision command and is independent of Submission Idempotency.

The idempotency key is associated with a deterministic request fingerprint containing semantic command inputs that can alter the decision, including ObservationId, expected version, assignment context, authenticated decision principal, outcome, applicable ReasonCode/Comment, and relevant RuleSet/Gate identity/version. Transport metadata and secrets are excluded.

Semantics:
- same key + same fingerprint → replay the original committed result; no second mutation.
- same key + different fingerprint → idempotency conflict; no second mutation or success audit.
- concurrent same-key requests → exactly one logical Decision commit.
- failed transaction → no successful Decision, Audit or Idempotency outcome is visible; retry remains possible per command semantics.

## Explicit non-goals
This binding does not freeze Oracle table/column names, SQL/DDL, transport DTO names, persistence technology, R04/R05 business criteria, read-model schema, or G02/G04 HOLD semantics.

## Implementation gate
GREEN implementation starts only after this Contract Binding is explicitly accepted as authoritative and the RED tests are present in the project test harness.

This document is a contract-binding proposal, not a new frozen architectural decision until explicitly approved.
