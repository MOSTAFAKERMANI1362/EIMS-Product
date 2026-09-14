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
- explicit recovery gates in this order: State Contract → Rule Contract → Event Contract → Mutation Contract.
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
- `AggregateSnapshot.RuleFacts` for authoritative server-loaded rule facts; request bodies cannot substitute persisted aggregate facts.
- outcome-aware `CommandEventBinding` supporting fail-closed `STATIC` and `OUTCOME` event resolution.
- contract tests using an in-memory transactional test adapter only.

A Product command with incomplete recovery metadata is rejected **before Store, Rule Evaluator, SoD evaluator or Mutation Planner is touched**. This prevents a later partial recovery step from accidentally turning a command into a mutating path.

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

`evaluation-assignments.complete`, `g04.vote`, and the remaining recovered commands do not yet have an accepted complete command-level state contract and therefore continue to fail at `P1_STATE_CONTRACT_NOT_RECOVERED`.

### 4.2 Event naming decisions

`ACR-P0-001` stabilizes the post-freeze event names:

- `CaseCreatedFromApprovedSource.v1`;
- `NeedCreatedFromQualifiedCase.v1`.

`ACR-P0-002` stabilizes the post-freeze G03 Need lifecycle events:

- `NeedSubmittedForG03Review.v1`;
- `NeedApprovedForIdeation.v1`;
- `NeedReturnedFromG03Review.v1`.

Both ACRs explicitly preserve provenance: these are controlled post-freeze architecture decisions, not recovered historical identities from the missing P0 Event Catalog. Event naming by itself cannot promote a Product command.

### 4.3 Wave 2 — G03 rule rebaseline

`P1_WAVE2_G03_RULE_REBASELINE_v1.0.json` formally re-baselines only the two G03 RuleSets whose rules are directly executable and explicit in frozen v6.360.

#### `needs.submit-g03`

RuleSet: `P1-G03-SUBMIT-REBASELINE-1.0`

The evaluator reads the Need definition from authoritative `AggregateSnapshot.RuleFacts`, not from the request body. Required frozen rules are:

- title trimmed length ≥ 10 and does not start with `نیاز مرتبط با`;
- owner trimmed length ≥ 3 and is not `مالک فرآیند مرتبط` / `مالک واحد موضوع`;
- current trimmed length ≥ 15 and does not contain `وضعیت موجود نیازمند تکمیل`;
- desired trimmed length ≥ 15 and does not contain `وضعیت مطلوب را تکمیل کنید`;
- gap trimmed length ≥ 10 and does not contain `نیازمند تکمیل`;
- current and desired are not equal after trimming.

A PASS means only that the Need is complete enough to be submitted for independent G03 review. It is not G03 approval.

#### `needs.g03-decision`

RuleSet: `P1-G03-DECISION-REBASELINE-1.0`

The persisted `g03ReviewStatus` must be `PENDING`. Request decision is restricted to `APPROVE` or `RETURN`.

- APPROVE requires `definitionComplete=YES`, `measurable=YES`, and `solutionBiasFree=YES`.
- RETURN requires an actionable trimmed note of at least 10 characters.
- the G03 reviewer scope remains Need completeness/readiness only; it is not technical/economic evaluation or solution selection.
- Need Owner cannot independently review the same Need at G03.
- final frozen routing is role-based: APPROVE → `IDEA_OWNER`, RETURN → `NEED_OWNER`. No old `ideaOwnerCandidate` requirement is restored.

At the end of Wave 2 the two G03 commands stopped at `P1_EVENT_CONTRACT_NOT_RECOVERED`. No audit label was promoted into a Domain Event identity.

### 4.4 Wave 3 — G03 event rebaseline

`P1_WAVE3_G03_EVENT_REBASELINE_v1.0.json` consumes approved `ACR-P0-002` and formally binds the G03 Domain Event contracts in P1:

- `needs.submit-g03` → static `NeedSubmittedForG03Review.v1`;
- `needs.g03-decision` → outcome-aware mapping:
  - APPROVE → `NeedApprovedForIdeation.v1`;
  - RETURN → `NeedReturnedFromG03Review.v1`.

The outcome-aware binding is fail-closed. A missing or unknown decision outcome resolves to no event and cannot fall back to a generic event identity.

For the two G03 commands, State, Rule and Event contracts are now recovered/re-baselined, but `MutationContractRecovered=false` remains mandatory. They therefore stop at:

`P1_MUTATION_CONTRACT_NOT_RECOVERED`

before Store, Rule Evaluator, SoD evaluator or Mutation Planner is touched. No event is emitted merely because an event binding exists.

Four other Wave 1 state-bound commands (`g01.decide`, `g02.decide`, `ideas.submit-g04`, `g04.final-decision`) remain at `P1_RULE_CONTRACT_NOT_RECOVERED`. All commands without accepted state contracts remain at `P1_STATE_CONTRACT_NOT_RECOVERED`.

No Product command is executable after Wave 3.

## 5. SoD explicitly recovered

The recovery kernel enforces directly stated invariants that can be evaluated with currently available authority context:

- Execution Owner cannot approve own completion.
- G04 Committee Member cannot act as final G04 decision authority in the same decision context.
- Knowledge Steward cannot publish as Knowledge Publisher in the same authority context.
- Benefit Owner cannot decide reward as Reward Committee in the same authority context.
- Need Owner cannot independently perform G03 review on the same Need.

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

G03 recovered RuleSet gate:

```powershell
dotnet build .\tests\EIMS.G03.RecoveryRules.ContractTests\EIMS.G03.RecoveryRules.ContractTests.csproj -c Release
dotnet run --project .\tests\EIMS.G03.RecoveryRules.ContractTests\EIMS.G03.RecoveryRules.ContractTests.csproj -c Release --no-build
```

P0→P1 recovery consistency gate:

```powershell
dotnet build .\tests\EIMS.P0P1.RecoveryGate.ContractTests\EIMS.P0P1.RecoveryGate.ContractTests.csproj -c Release
dotnet run --project .\tests\EIMS.P0P1.RecoveryGate.ContractTests\EIMS.P0P1.RecoveryGate.ContractTests.csproj -c Release --no-build -- .\recovery\p0\P0_MACHINE_CONTRACT_RECOVERY_v1.0.json .\recovery\p1-wave1\P1_WAVE1_STATE_REBASELINE_ACCEPTANCE_v1.0.json .\recovery\p1-wave2\P1_WAVE2_G03_RULE_REBASELINE_v1.0.json .\recovery\p1-wave3\P1_WAVE3_G03_EVENT_REBASELINE_v1.0.json
```

All gates and the repository Security Pipeline must pass before merge.

## 8. Exit condition for full P1 recovery

P1 Recovery can be promoted from **KERNEL RECOVERED** to **P1 PHYSICALLY COMPOSED** only when all of the following are true:

- required machine-readable P0 state/rule/event/role contracts are recovered or formally re-baselined command-by-command;
- the 28-command catalog discrepancy is resolved without guessing;
- Product command mutation planners are bound to authoritative state/rule/event contracts;
- P1 tests PASS on .NET SDK 10.0.302;
- P5 physically composes the recovered P1 runtime;
- no command can bypass Server Authority;
- live environment evidence proves the required Domain/Oracle/hosting bindings for Network Pilot.
