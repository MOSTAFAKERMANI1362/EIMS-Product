# EIMS P0 Machine Contract Recovery — Source Registry Wave 2 v1.0

**Scope:** Portfolio → Execution → Benefit → Knowledge  
**Status:** RECOVERY ARTIFACT / NOT ORIGINAL P0 PACKAGE  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Authority order
1. Frozen v6.360 Executable Product Specification.
2. P0 Product Contract v1.0.
3. P0 API Contract Draft v1.0.
4. P0 Gaps/TBD v1.0.
5. Approved engineering repositories only as supporting evidence.

## Evidence classifications
- `CONFIRMED_V6_360`: explicit final frozen product behavior.
- `CONFIRMED_P0_CONTRACT`: explicit P0 invariant/SoD/product rule.
- `PROPOSED_P0_API`: proposed server command/role contract from P0 API draft.
- `CONTROLLED_MIGRATION_DECISION`: later productization decision; not reconstructed frozen behavior.
- `LEGACY_SUPERSEDED`: older prototype authority/role mapping contradicted by later freeze/P0 authority.
- `TBD_UNRECOVERED_SERVER_COMMAND`: frozen behavior exists but no P0 mutating command identity is recovered; server implementation must fail closed until explicitly baselined.
- `CONFIGURABLE_TBD`: product rule explicitly left configurable/TBD by P0.

## Recovered post-G04 semantics

### Portfolio
- `PortfolioIntakeCandidate`, assignment/membership, execution recommendation and execution handoff remain distinct.
- Candidate assignment creates a PENDING assignment and explicitly does **not** create membership.
- Membership decision states include `ACCEPTED`, `REJECTED`, `DEFERRED`; frozen domain events exist for each.
- Active recommendation requires accepted membership.
- Recommendation approval and approved baseline precede execution handoff.
- Handoff emits `ExecutionHandoffRequested.v1` and creates a PLANNING execution; `ExecutionCreatedFromRecommendation.v1` is frozen.
- P0 server command recovered: `portfolio.assign-accept` / role `PORTFOLIO_MANAGER`.

### Execution
- Start requires approved Charter + approved Plan baseline.
- Progress update is valid in ACTIVE.
- 100% progress is only a prerequisite to submit completion; it is not completion itself.
- Completion submission: ACTIVE → COMPLETION_REVIEW.
- P0 requires independent `EXECUTION_COMPLETION_REVIEWER`; Execution Owner self-approval is forbidden.
- Frozen older local role mapping that allowed `approveCompletion65` under EXECUTION_OWNER is classified `LEGACY_SUPERSEDED` for server authority.
- `COMPLETED` is not terminal archive; closure remains required and `CLOSED` is terminal.
- Completed execution may request Benefit handoff; frozen domain event `BenefitHandoffRequested.v1` creates a Benefit obligation in `OBLIGATION_PENDING_ACCEPTANCE`.

### Benefit
- Sequential frozen state path:
  `OBLIGATION_PENDING_ACCEPTANCE → BASELINE_REQUIRED → PLAN_REQUIRED → MEASUREMENT_PENDING → MEASURED → VERIFIED → VALIDATED → REALIZED → CLOSED`.
- P0 server commands/roles recovered: accept, measure, verify, attribution, realize.
- Baseline definition, plan approval and close are frozen product behaviors but no explicit P0 command identity is recovered; they remain `TBD_UNRECOVERED_SERVER_COMMAND`.
- Verification is independent from attribution and realization.
- Non-financial benefit is first-class.
- Benefit closure requires REALIZED state plus published Knowledge evidence in the frozen workflow.

### Knowledge
- Validation and Publication are separate.
- P0 server commands: `knowledge.validate` / `KNOWLEDGE_STEWARD`; `knowledge.publish` / `KNOWLEDGE_PUBLISHER`.
- Validation APPROVE → VALIDATED; correction/return → DRAFT.
- Publication PUBLISH → PUBLISHED; return → DRAFT.
- Final frozen UAT explicitly verifies publisher role is `KNOWLEDGE_PUBLISHER` and Steward/Publisher are separated.
- A legacy validation-return mapping to BENEFIT_OWNER exists in older prototype code, but P0 says primary Knowledge author is not Benefit Owner and author resolution defaults to Need Owner/domain specialist with exact selection configurable. Therefore that legacy correction-owner mapping is not recovered as product authority.

## Controlled migration decisions — not frozen reconstruction
- Preserve `PortfolioEligibilityService` logic but invoke it automatically after `G04 Approved` in normal product operation.
- Remove ordinary-user manual PortfolioEligibilityService execution; keep manual execution only Admin/Test/UAT.
- No post-G04 workflow simplification occurs in recovery. A Data Value Audit is required before any future reduction of roles/forms/questions.

## Fail-closed rules
- No prototype function without recovered P0 command identity may become a server command silently.
- No legacy UI role guard overrides P0 SoD/API authority.
- No inferred Event identity is added to Outbox.
- No state or role is removed merely to simplify UX.
- v6.360 is not edited by this operation.
