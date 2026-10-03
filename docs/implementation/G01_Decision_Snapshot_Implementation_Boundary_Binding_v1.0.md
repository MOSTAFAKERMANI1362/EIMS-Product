# EIMS G01 — Decision Snapshot Implementation Boundary Binding

Version: 1.0
Status: APPROVED BINDING — RED gate only
Scope: A01-EIMS → VS-01 → Observation → G01
Authority: V021 + frozen D01-D04 + approved G01 HOLD removal + approved G01 Snapshot/Decision Idempotency Contract

## 1. Purpose
This artifact binds the minimum implementation boundary required to make the approved G01 Decision Snapshot contract executable.
It does not define API DTO names, persistence technology, Oracle schema, SQL/DDL, or an implementation class hierarchy.

## 2. Binding
A G01 Decision Snapshot is a first-class semantic artifact of the G01 Decision transaction.
The implementation boundary MUST provide these three capabilities:

1. Production creation boundary — create the Snapshot from authoritative decision-time inputs/evaluations; do not reconstruct it later from mutable Observation state.
2. Atomic commit boundary — Snapshot commit is part of the same atomic transaction boundary as Decision + Decision Audit + Decision Idempotency outcome + required domain mutation/event staging.
3. Committed-state evidence boundary — the test harness can inspect committed Snapshot state through a read-only semantic evidence surface. Concrete DTO/class/property names remain implementation details.

## 3. Required semantic identity
Every committed Snapshot must be addressable by SnapshotId, DecisionId, ObservationId, and evaluated ObservationVersion.
The Snapshot retains the exact RuleSet identity/version and the four G01 rule results R02-R05 used for the decision.

## 4. Immutability boundary
After successful Decision commit, Snapshot content cannot be changed by later Observation mutation and a later Observation version does not alter the Snapshot.
Tests must verify committed-state immutability, not object-reference immutability or method-call choreography.

## 5. Authorization evidence boundary
The Snapshot records only the authorization evidence required to explain the decision: authenticated principal identity and applicable role/capability/scope/assignment evidence.
It must not store secrets, credentials, tokens, or raw authentication claims.

## 6. ReasonCode / Comment boundary
APPROVE: ReasonCode absent. RETURN: ReasonCode required. REJECT: ReasonCode required.
Comment remains a separate command field and must not be substituted for ReasonCode.

## 7. Fingerprint boundary
The Snapshot has a deterministic Fingerprint representing its semantic content.
The fingerprint is an integrity representation, not a transport identifier and not a replacement for Decision Idempotency.

## 8. RED-test consequence
These tests may bind to the semantic committed-state boundary without freezing implementation names:
- G01-SNAPSHOT-RED-01 — APPROVE snapshot completeness
- G01-SNAPSHOT-RED-02 — RETURN snapshot captures ReasonCode
- G01-SNAPSHOT-RED-03 — REJECT snapshot captures ReasonCode
- G01-SNAPSHOT-RED-04 — snapshot immutability
- G01-SNAPSHOT-RED-05 — snapshot binds evaluated Observation business version

Decision Idempotency RED tests remain governed by the already-approved contract and are not replaced by this artifact.

## 9. Non-goals
This binding does not freeze Oracle tables/columns/packages, SQL/DDL/migrations, API transport DTOs, concrete C# type/property names, persistence technology, R04/R05 business criteria, G02/G04 behavior, or Observation final state after APPROVE.

## 10. Gate
This binding authorizes creation of the RED tests only.
It does NOT authorize GREEN production implementation.
GREEN requires a separate explicit gate after RED evidence is captured and reviewed.