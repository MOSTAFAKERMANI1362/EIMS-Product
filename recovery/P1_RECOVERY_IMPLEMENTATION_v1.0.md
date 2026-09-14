# EIMS P1 Recovery Implementation v1.0

**Status:** RECOVERY IMPLEMENTATION / NOT ORIGINAL P1 PACKAGE  
**Branch:** `recovery/p1-authority-kernel`  
**Frozen product source:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen source SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## 1. Why recovery exists

The P1 Completion Review states that a Backend Authority Reference was previously created, including an authority pipeline, idempotency, Audit/Outbox atomicity, a 28-command catalog, .NET 10 reference solution and contract tests. The physical P1 source/package is not currently recoverable from the controlled GitHub repository or available File Library search results.

This implementation therefore **does not claim to be the original P1 artifact**. It is a new recovery implementation derived only from recovered P0/P1 evidence.

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

## 4. Deliberately fail-closed / not recovered

### Product command state/rule/event semantics

The recovered P0 API contract explicitly exposes 21 mutating command endpoints/roles. The prior P1 completion review states the original P1 catalog contained 28 commands. The seven additional command identities have not been recovered.

The machine-readable P0 State Machine, Role/Permission, Rule and Event JSON files are referenced by the Product Contract but are not currently physically available in the accessible File Library.

Therefore `RecoveredApiCommandCatalog`:

- records the 21 command identities/roles recovered from the P0 API draft;
- marks product state/rule/event contracts as unrecovered;
- contains no invented business transition/event semantics;
- returns fail-closed before product mutation.

`p1.authority.runtime` MUST NOT be marked Ready merely because the generic kernel compiles.

## 5. SoD explicitly recovered

The recovery kernel enforces the directly stated invariants that can be evaluated with the currently available authority context:

- Execution Owner cannot approve own completion.
- G04 Committee Member cannot act as final G04 decision authority in the same decision context.
- Knowledge Steward cannot publish as Knowledge Publisher in the same authority context.
- Benefit Owner cannot decide reward as Reward Committee in the same authority context.

Cross-aggregate conflict checks that require richer domain links remain for physical persistence/domain composition and must not be guessed.

## 6. P2/P3/P4 boundary

This recovery does not implement:

- Oracle physical schema/provider/transaction adapter (P2);
- Windows Integrated Authentication and claim mapping (P3);
- HR/Organization import mapping (P4).

Those remain separate controlled packages.

## 7. Verification gate

Required before merge:

```powershell
dotnet build .\tests\EIMS.Authority.Recovery.ContractTests\EIMS.Authority.Recovery.ContractTests.csproj -c Release
dotnet run --project .\tests\EIMS.Authority.Recovery.ContractTests\EIMS.Authority.Recovery.ContractTests.csproj -c Release --no-build
```

Expected: all P1 Recovery contract tests PASS, plus repository security pipeline PASS.

## 8. Exit condition for full P1 recovery

P1 Recovery can be promoted from **KERNEL RECOVERED** to **P1 PHYSICALLY COMPOSED** only when all of the following are true:

- missing machine-readable P0 state/rule/event/role contracts are recovered or formally re-baselined;
- the 28-command catalog discrepancy is resolved without guessing;
- product command mutation planners are bound to authoritative state/rule/event contracts;
- P1 tests PASS on .NET SDK 10.0.302;
- P5 physically composes the recovered P1 runtime;
- no command can bypass Server Authority.
