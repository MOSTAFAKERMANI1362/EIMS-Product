# EIMS G01 — Approved Contract + RED Handoff

Status: APPROVED BY PROJECT OWNER IN CHAT — use as the authoritative G01 Snapshot/Decision Idempotency contract for the next gate.
Purpose: transfer the already-approved contract and RED specification to the Codex workspace without redesign.

## Governance

The project owner explicitly approved `G01_Decision_Snapshot_Idempotency_Contract_Binding_v1.0.md` after review. The source artifact's earlier PROPOSED status is superseded by this explicit project approval. Do not create a new architectural decision from this handoff.

No GREEN implementation is authorized merely by this handoff. First execute the RED/readiness gate.

## Source files

- `G01_Decision_Snapshot_Idempotency_Contract_Binding_v1.0.md`
- `G01_Decision_Snapshot_Idempotency_RED_Test_Spec_v1.0.md`
- `EIMS_Architecture_Transfer_Package_V021.md`

## Approved Contract — verbatim source

# EIMS — G01 Decision Snapshot & Decision Idempotency Contract Binding

Version: 1.0
Status: PROPOSED CONTRACT BINDING — implementation not yet authorized
Scope: VS-01 / G01 only
Authority: V021 + frozen D01-D04 + approved G01 HOLD removal

## 1. Purpose

This artifact binds the minimum semantic contract required before implementing G01 Decision Snapshot and G01 Decision Idempotency. It does not change Oracle schema, API DTOs, authentication architecture, or production persistence.

## 2. Frozen prerequisites

- RuleSetId = `G01-INQ`
- RuleSetVersion = `1.0`
- G01 outcomes = `APPROVE`, `RETURN`, `REJECT`
- `HOLD` is removed from G01.
- APPROVE requires `G01_COMPLETE`, no blocking FAIL/ERROR, valid authorization, assignment, state, transition and SoD.
- RETURN and REJECT require a non-empty ReasonCode.
- Comment and ReasonCode are separate concepts.
- APPROVE maps to Case `UNDER_REVIEW` and the frozen event `CaseCreatedFromApprovedSource.v1`.
- Submission Idempotency is distinct from Decision Idempotency.

## 3. Decision Snapshot contract

A Decision Snapshot is an immutable, decision-time representation of the inputs and evaluations that materially justify the G01 decision. It is not a copy of the entire Observation and is not a UI snapshot.

Required semantic fields:

| Field | Requirement |
|---|---|
| SnapshotId | Unique immutable identifier |
| DecisionId | Owning G01 Decision identifier |
| ObservationId | Canonical source identity |
| ObservationVersion | Business version evaluated |
| RuleSetId | Must be `G01-INQ` |
| RuleSetVersion | Must be `1.0` for current contract |
| GateOutcome | `G01_COMPLETE`, `G01_INCOMPLETE`, or `G01_BLOCKED` |
| RuleExecutions | Immutable result set for all four G01 rules R02-R05 |
| AuthorizationContext | Principal, role/capability/scope/assignment evidence needed to explain authorization |
| DecisionOutcome | APPROVE / RETURN / REJECT |
| ReasonCode | Required for RETURN/REJECT; absent for APPROVE |
| Comment | Separate optional/required-by-command field according to command contract; not interchangeable with ReasonCode |
| CreatedAt | Decision-time timestamp |
| SchemaVersion | Snapshot contract version |
| Fingerprint | Deterministic integrity representation of the snapshot content |

### Snapshot invariants

1. Snapshot is immutable after successful decision commit.
2. Snapshot references the evaluated Observation business version; it does not silently follow later Observation versions.
3. Snapshot records RuleSet identity/version and every G01 rule result used by the decision.
4. `NOT_APPLICABLE` is not converted to `PASS`.
5. `ERROR` is not converted to `FAIL`.
6. Snapshot must not contain secrets, credentials, tokens, or raw authentication claims.
7. Snapshot must not depend on prototype-only field names.
8. A Decision cannot be considered committed unless its Snapshot is committed in the same transaction boundary as the Decision, Audit, Idempotency outcome, and required domain mutation/event staging.

## 4. Decision Idempotency contract

Decision Idempotency applies only to the G01 Decision command. It is independent from Observation Submission Idempotency.

### Key

A caller supplies a non-empty idempotency key scoped to the G01 Decision command. The server associates the key with a deterministic request fingerprint.

The fingerprint must include the semantic command inputs that can change the decision, at minimum:

- ObservationId
- expected Observation version
- assignment identity/context
- authenticated decision principal
- decision outcome
- ReasonCode when applicable
- command-level comment when applicable
- relevant Gate/RuleSet identity and version

The fingerprint must exclude transport-only metadata and secrets.

### Replay semantics

**Same key + same fingerprint:** return the previously committed Decision result without applying the mutation again.

**Same key + different fingerprint:** reject as idempotency conflict. No domain mutation, audit success event, or new Decision may be committed.

**First key use:** execute the normal G01 Decision transaction.

**Concurrent same-key requests:** exactly one logical Decision may commit; all other requests resolve to the same committed outcome or an explicit conflict/retry result without duplicate mutation.

### Failure semantics

If the transaction fails before commit:

- no Decision is considered committed;
- no successful Decision Audit is committed;
- no successful Decision Idempotency outcome is visible;
- the key may be retried according to the command's retry semantics.

An idempotency record must never claim success when the corresponding Decision and required side effects were not atomically committed.

## 5. Transaction boundary

The production boundary must guarantee atomic visibility of:

`Decision + Snapshot + Decision Audit + Decision Idempotency outcome + required Case/Event mutation`

This is a contract requirement only. It does not authorize an Oracle implementation or schema change.

## 6. Explicit non-goals

This binding does not freeze:

- Oracle table/column names
- SQL/DDL
- API transport DTO names
- persistence technology
- R04/R05 business criteria beyond their existing rule identities
- G01 read-model projection schema
- G02/G04 HOLD semantics

## 7. Required RED tests

The following behaviors must fail against the current implementation until the corresponding implementation exists:

1. APPROVE produces an immutable Snapshot containing RuleSet identity/version, four rule results, gate outcome, observation version, authorization evidence and Decision outcome.
2. RETURN produces a Snapshot containing required ReasonCode.
3. REJECT produces a Snapshot containing required ReasonCode.
4. Snapshot cannot be mutated after commit.
5. Same Decision idempotency key + same fingerprint produces one committed Decision and replay of the original result.
6. Same Decision idempotency key + different fingerprint produces an idempotency conflict and no second mutation.
7. Concurrent same-key requests produce one committed Decision.
8. Decision transaction failure does not leave a successful idempotency outcome or successful Decision Audit.
9. Submission Idempotency and Decision Idempotency remain independent namespaces/commands.
10. Snapshot records the evaluated Observation business version and does not follow a later version implicitly.

## 8. Implementation gate

GREEN implementation may begin only after this Contract Binding is accepted as the authoritative G01 Snapshot/Decision Idempotency contract and the RED tests are present in the project test harness.

## 9. Governance

This artifact is a contract-binding proposal derived from the current project baseline. It must not be treated as a new frozen architectural decision until explicitly approved.

## RED Test Specification — verbatim source

# G01 Decision Snapshot & Decision Idempotency — RED Test Specification

Status: RED / implementation intentionally absent

## Test IDs

- G01-SNAPSHOT-RED-01 — APPROVE snapshot completeness
- G01-SNAPSHOT-RED-02 — RETURN snapshot captures ReasonCode
- G01-SNAPSHOT-RED-03 — REJECT snapshot captures ReasonCode
- G01-SNAPSHOT-RED-04 — snapshot immutability
- G01-IDEMP-RED-01 — same key/same fingerprint replay
- G01-IDEMP-RED-02 — same key/different fingerprint conflict
- G01-IDEMP-RED-03 — concurrent same-key single commit
- G01-IDEMP-RED-04 — failed transaction leaves no successful idempotency outcome
- G01-IDEMP-RED-05 — submission and decision idempotency independence
- G01-SNAPSHOT-RED-05 — snapshot binds evaluated Observation business version

## State-based assertions

These tests must assert persisted/committed state and returned command results. They must not assert method-call choreography.

## RED condition

The current implementation does not expose an authoritative G01 Decision Snapshot + Decision Idempotency implementation satisfying the contract above. Therefore these tests are expected to fail until GREEN implementation is added.

No test may be weakened merely to accommodate the existing implementation.

## Codex Gate

1. Preserve branch and all uncommitted work.
2. Do not reset, clean, overwrite, or discard worktree changes.
3. Read this handoff and reconcile it with V021 and frozen D01-D04.
4. Run/reconcile the ten RED tests against the current implementation.
5. Do not implement GREEN yet.
6. Do not change Oracle, schema, DDL, migration, or unrelated modules.
7. Do not weaken tests.
8. If any conflict with V021/frozen decisions is found, STOP and report the conflict; do not decide autonomously.
9. Report exact coverage, test evidence, blockers, and GREEN readiness.
