# EIMS P1 Recovery Implementation v1.0

**Status:** RECOVERY IMPLEMENTATION / NOT ORIGINAL P1 PACKAGE  
**Frozen product source:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen source SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## 1. Why recovery exists

The P1 Completion Review states that a Backend Authority Reference was previously created, including an authority pipeline, idempotency, Audit/Outbox atomicity, a 28-command catalog, .NET 10 reference solution and contract tests. The physical original P1 source/package is not currently recoverable from the controlled repository.

This implementation therefore **does not claim to be the original P1 artifact**. It is a new recovery implementation derived only from controlled P0/P1 evidence and later explicit rebaseline decisions.

## 2. Recovered authoritative requirements

Every mutating command must enforce, server-side:

1. authenticated identity;
2. PersonID / Role Assignment context;
3. role and scope;
4. aggregate existence;
5. optimistic `expectedVersion`;
6. allowed state;
7. Separation of Duties;
8. hard rules / RuleSet;
9. server-side idempotency;
10. one atomic persistence boundary for State + Audit + Outbox;
11. versioned event after commit.

Client-side visibility is never an authorization boundary.

## 3. Implemented in this recovery kernel

- `AuthorityActor` with PersonID, network identity, identity source, AssignmentID, roles and scopes.
- `AuthorityCommand` with aggregate, expected version, idempotency key and correlation.
- `AuthorityKernel` fail-closed pipeline.
- role/scope check.
- optimistic version check.
- state allow-list check.
- explicit SoD checks recoverable from P0 invariants.
- pluggable rule evaluator.
- pluggable mutation planner.
- persistence abstraction.
- exact idempotent replay and key/payload conflict handling.
- atomic commit contract for Aggregate + Audit + Outbox + idempotency record.
- Audit authority context: PersonID + Role + Assignment + IdentitySource + EntityVersion + RuleSet + Timestamp + Correlation.
- contract tests using an in-memory transactional test adapter only.

## 4. Product-command recovery status

The recovered P0 API contract exposes 21 mutating command identities/roles. The historical P1 completion review states that the original P1 catalog contained 28 commands. The seven additional command identities remain unidentified and are not invented.

The original machine-readable P0 State Machine, Role/Permission, Rule and Event JSON files remain physically unavailable. They are not represented as recovered originals.

### 4.1 Wave 1 formal state rebaseline

`P1_WAVE1_STATE_REBASELINE_ACCEPTANCE_v1.0.json` formally accepts command-level state evidence already recovered from frozen v6.360 for six commands:

- `g01.decide` — allowed from `SUBMITTED` or `SUBMITTED_FOR_G01`;
- `g02.decide` — allowed from `UNDER_REVIEW`;
- `needs.submit-g03` — allowed from `DRAFT`;
- `needs.g03-decision` — allowed from `PENDING_G03_REVIEW`;
- `ideas.submit-g04` — allowed from `DRAFT` or `RETURNED`;
- `g04.final-decision` — allowed from `UNDER_REVIEW`.

This is a **new recovery rebaseline acceptance**, not a claim that the missing original P0 state-machine file has been recovered.

For these six commands, `StateContractRecovered=true`, but `RuleContractRecovered=false`. The runtime therefore still fails closed at `P1_RULE_CONTRACT_NOT_RECOVERED` before Store, Rule Evaluator, SoD evaluator or Mutation Planner is touched.

`evaluation-assignments.complete`, `g04.vote`, and the remaining recovered commands do not yet have an accepted complete command-level state contract and therefore continue to fail with `P1_STATE_CONTRACT_NOT_RECOVERED`.

### 4.2 Event naming decision

`ACR-P0-001` stabilizes the post-freeze event names:

- `CaseCreatedFromApprovedSource.v1`;
- `NeedCreatedFromQualifiedCase.v1`.

The ACR explicitly records that the historical original event mapping in v6.360 was TBD. The names are therefore a controlled new architecture decision, not recovered historical identities.

The P1 runtime command policy still retains `UNRECOVERED_EVENT_IDENTITY` until an outcome-aware mutation/event planner is bound. Event naming alone cannot promote a command.

### 4.3 Rule contracts remain the immediate blocker

No Product RuleSet is reconstructed by inference. The six state-bound commands remain non-executable until their command-specific hard-rule semantics are recovered or formally re-baselined and verified.

`p1.authority.runtime` MUST NOT be marked Ready merely because the generic kernel or state-binding layer compiles.

## 5. SoD explicitly recovered

The recovery kernel enforces the directly stated invariants that can be evaluated with the currently available authority context:

- Execution Owner cannot approve own completion.
- G04 Committee Member cannot act as final G04 decision authority in the same decision context.
- Knowledge Steward cannot publish as Knowledge Publisher in the same authority context.
- Benefit Owner cannot decide reward as Reward Committee in the same authority context.

Cross-aggregate conflict checks that require richer domain links remain for physical persistence/domain composition and must not be guessed.

## 6. P2/P3/P4 boundary

This recovery does not by itself prove live production composition of:

- Oracle physical provider/transaction binding (P2);
- Windows Integrated Authentication and live Domain claim mapping (P3);
- live HR/Organization import/reconciliation (P4).

Those remain separate controlled packages and environment gates.

## 7. Verification gates

P1 kernel contract gate:

```powershell
dotnet build .\tests\EIMS.Authority.Recovery.ContractTests\EIMS.Authority.Recovery.ContractTests.csproj -c Release
dotnet run --project .\tests\EIMS.Authority.Recovery.ContractTests\EIMS.Authority.Recovery.ContractTests.csproj -c Release --no-build
```

P0→P1 recovery consistency gate:

```powershell
dotnet build .\tests\EIMS.P0P1.RecoveryGate.ContractTests\EIMS.P0P1.RecoveryGate.ContractTests.csproj -c Release
dotnet run --project .\tests\EIMS.P0P1.RecoveryGate.ContractTests\EIMS.P0P1.RecoveryGate.ContractTests.csproj -c Release --no-build -- .\recovery\p0\P0_MACHINE_CONTRACT_RECOVERY_v1.0.json .\recovery\p1-wave1\P1_WAVE1_STATE_REBASELINE_ACCEPTANCE_v1.0.json
```

Both gates and the repository Security Pipeline must pass before merge.

## 8. Exit condition for full P1 recovery

P1 Recovery can be promoted from **KERNEL RECOVERED** to **P1 PHYSICALLY COMPOSED** only when all of the following are true:

- required machine-readable P0 state/rule/event/role contracts are recovered or formally re-baselined command-by-command;
- the 28-command catalog discrepancy is resolved without guessing;
- Product command mutation planners are bound to authoritative state/rule/event contracts;
- P1 tests PASS on .NET SDK 10.0.302;
- P5 physically composes the recovered P1 runtime;
- no command can bypass Server Authority;
- live environment evidence proves the required Domain/Oracle/hosting bindings for Network Pilot.
