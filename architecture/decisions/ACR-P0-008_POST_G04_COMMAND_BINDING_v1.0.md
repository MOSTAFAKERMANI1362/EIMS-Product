# ACR-P0-008 — Post-G04 Portfolio→Knowledge Command Binding

**Version:** 1.0  
**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** Post-freeze architecture decision  
**Frozen product source:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen source SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Purpose

This ACR closes the machine-command and server-authority binding gap for the frozen post-G04 main thread:

`Portfolio → Execution → Benefit → Knowledge`

It does **not** redesign the business workflow and does not modify v6.360. It only stabilizes command/event boundaries needed for P1 runtime implementation.

## Authority basis

The decision reconciles:

1. frozen v6.360 product behavior;
2. `EIMS_P0_API_Contract_Draft_v1.0.yaml`;
3. `EIMS_P0_Gaps_TBD_v1.0.md`;
4. `P0_PORTFOLIO_KNOWLEDGE_RECOVERED_CONTRACT_v1.0.json`.

Where an older P0 API command grouped multiple frozen actions, the frozen product semantics take precedence and the server command is split rather than collapsing domain states.

## Grouped command decisions

### `portfolio.assign-accept`

This grouped command is **not** implemented as one mutation.

Frozen semantics require separate controls for:

- candidate assignment;
- membership decision;
- execution recommendation generation;
- recommendation approval;
- approved portfolio baseline binding;
- execution handoff.

Assignment must not create membership, membership acceptance must precede recommendation, and handoff requires both an approved recommendation and approved baseline.

### `executions.prepare`

This grouped command is **not** implemented as one mutation.

The frozen workflow separately records:

- Execution Charter approval;
- Execution Plan Baseline approval;
- Execution start.

Start is allowed only after both approvals.

## Automatic Portfolio Eligibility

`PortfolioEligibilityService` remains an internal system action triggered by committed `IdeaApprovedForPortfolio.v1`.

Rules:

- ordinary users do not invoke it manually;
- pass requires approved Idea, no hard readiness gap and no policy exclusion;
- one candidate is created for one approved Idea version;
- retry is idempotent on Idea + approved version;
- manual execution remains restricted to Admin/Test/UAT tooling only.

## Stabilized command families

### Portfolio

- `portfolio.assign-candidate`
- `portfolio.membership-decision`
- `portfolio.generate-execution-recommendation`
- `portfolio.approve-execution-recommendation`
- `portfolio.bind-approved-baseline`
- `portfolio.request-execution-handoff`

Authority: `PORTFOLIO_MANAGER`.

### Execution

- `executions.approve-charter`
- `executions.approve-plan-baseline`
- `executions.start`
- `executions.progress`
- `executions.submit-completion`
- `executions.completion-review`
- `executions.request-benefit-handoff`
- `executions.begin-closure`
- `executions.close`

`EXECUTION_COMPLETION_REVIEWER` remains independent from `EXECUTION_OWNER`; the owner may submit but may not approve their own completion.

### Benefit

- `benefits.accept`
- `benefits.set-baseline`
- `benefits.approve-measurement-plan`
- `benefits.measure`
- `benefits.verify`
- `benefits.attribution`
- `benefits.realize`
- `benefits.close`

The lifecycle remains:

`OBLIGATION_PENDING_ACCEPTANCE → BASELINE_REQUIRED → PLAN_REQUIRED → MEASUREMENT_PENDING → MEASURED → VERIFIED → VALIDATED → REALIZED → CLOSED`

Measurement, verification, attribution and realization remain separate. Non-financial benefits remain first-class. Benefit closure requires a published Knowledge Asset and publication evidence.

### Knowledge

- `knowledge.create-draft`
- `knowledge.validate`
- `knowledge.publish`

Knowledge author authority is resolved server-side by policy. Default candidates are Need Owner or Domain Expert, with Idea Owner permitted when configured. The legacy prototype path in which Benefit Owner directly acted as primary author is not promoted to server authority.

Validation authority: `KNOWLEDGE_STEWARD`.  
Publication authority: `KNOWLEDGE_PUBLISHER`.

Validation and publication remain separate responsibilities. Return from validation or publication routes to the resolved author policy, not automatically to Benefit Owner.

## State invariants

### Execution

`PLANNING → ACTIVE → COMPLETION_REVIEW → COMPLETED → CLOSURE_IN_PROGRESS → CLOSED`

- 100% progress is not completion.
- `COMPLETED` is not `CLOSED`.
- completion approval requires independent review.

### Benefit

`OBLIGATION_PENDING_ACCEPTANCE → BASELINE_REQUIRED → PLAN_REQUIRED → MEASUREMENT_PENDING → MEASURED → VERIFIED → VALIDATED → REALIZED → CLOSED`

### Knowledge

`DRAFT → VALIDATED → PUBLISHED`

## Server authority controls

Every mutating command is subject to the existing P1 authority contract:

- identity comes from server-resolved P3 Windows Identity;
- exact Assignment context is required;
- client-supplied Role/Scope is never trusted;
- optimistic `expectedVersion` is required;
- server idempotency key is required;
- State + Decision Evidence + Audit + Outbox + Idempotency commit atomically;
- missing authority/state evidence fails closed;
- Digital Thread identifiers are preserved.

## Event policy

Previously explicit frozen events retain their names, including:

- `PortfolioMembershipAccepted.v1`
- `PortfolioMembershipRejected.v1`
- `PortfolioMembershipDeferred.v1`
- `ExecutionHandoffRequested.v1`
- `ExecutionCreatedFromRecommendation.v1`
- `BenefitHandoffRequested.v1`

ACR-P0-008 assigns versioned event identities to previously unbound server transitions. These identities are machine-contract decisions, not new user-visible workflow stages.

## Runtime gate

P1 post-G04 implementation may start only after this ACR's verifier and Security CI pass.

This ACR does **not** make the Network Pilot ready. OP-05 final PASS remains blocked until real OP-04 `PILOT_ENVIRONMENT_EVIDENCE` closes the organizational environment gates. Test-only or LAB evidence cannot satisfy final OP-05 acceptance.
