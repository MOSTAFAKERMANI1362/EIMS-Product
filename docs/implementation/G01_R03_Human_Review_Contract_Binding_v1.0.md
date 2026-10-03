# G01 R03 Human Review Contract Binding v1.0

Status: P1 BINDING — DESIGN ARTIFACT

## Scope

This binding defines the G01 R03 contract for DUPLICATE_HISTORY_CHECKED using a human-review evidence boundary. It does not introduce a duplicate-detection algorithm.

## Rule

R03 — DUPLICATE_HISTORY_CHECKED

The rule requires a valid human review of relevant duplicate/history evidence before a final R03 result can be established.

## Review Input

A valid review record shall bind to the current Observation and provide, at minimum:

- ReviewPerformed
- ReviewScope or EvidenceReference
- ReviewResult
- Reviewer identity
- Review timestamp / correlation

The exact persistence representation is not defined here.

## ReviewResult Vocabulary

The following review outcomes are recognized at the architecture/assessment boundary:

- DUPLICATE
- DIFFERENT
- UNKNOWN
- CONFLICT
- AMBIGUOUS

These values are not themselves G01 canonical RuleExecution results.

## Canonical G01 Result Mapping

The following mapping is bound:

- DUPLICATE → FAIL
- DIFFERENT → PASS
- UNKNOWN → WARNING

The following mappings remain OPEN and are not authorized for implementation:

- CONFLICT → OPEN
- AMBIGUOUS → OPEN

Absence of a valid human review does not map to PASS, FAIL, WARNING, NOT_APPLICABLE, or ERROR. At Gate level it prevents a valid complete outcome and may require G01_BLOCKED according to the eventual Gate Aggregation contract.

## Boundaries

This artifact does not define:

- a new Rule Result enum
- a new G01 state
- Duplicate Detection / Search algorithm
- automatic duplicate determination
- Gate Aggregation
- APPROVE/RETURN/REJECT transitions
- ReasonCode catalog
- API endpoint
- Oracle table/DDL/migration
- authorization changes
- snapshot schema
- audit schema

## Test Boundary

Executable RED tests may cover:

1. DIFFERENT produces PASS.
2. DUPLICATE produces FAIL.
3. UNKNOWN produces WARNING.
4. Missing human review does not produce a positive/negative canonical result.

Tests for CONFLICT and AMBIGUOUS remain blocked until their mapping is separately decided.

## Source Basis

The binding reuses the recovered G01 source semantics: R03 is a human review of duplicate/history evidence. Architecture-level duplicate outcomes include DUPLICATE, DIFFERENT, UNKNOWN, CONFLICT, and AMBIGUOUS. These architecture outcomes are intentionally kept separate from G01 canonical RuleExecution results.

## Change Control

This is a P1 contract binding. It does not reopen frozen G01 decisions. Any change to the OPEN mappings requires a separate Proposal/Decision.
