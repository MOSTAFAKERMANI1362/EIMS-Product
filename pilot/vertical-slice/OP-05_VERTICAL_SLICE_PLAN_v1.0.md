# OP-05 — Production Vertical Slice Plan v1.0

## هدف

اثبات یک مسیر واقعی و کامل EIMS روی Backend/Identity/Persistence واقعی، بدون گسترش هم‌زمان همه قابلیت‌ها.

مسیر مرجع:

`ImprovementSource → Case → Need → Idea → G04 → Portfolio → Execution → Benefit → Knowledge`

## اصل اجرایی

تا زمانی که این Vertical Slice پایدار و تکرارپذیر نشده، توسعه افقی Featureهای جدید متوقف می‌ماند.

## Pre-Conditions

OP-05 فقط وقتی وارد اجرای واقعی می‌شود که حداقل Evidence لازم OP-04 برای این موارد موجود باشد:

- Windows/IIS pilot host
- Windows Integrated Authentication path
- PersonID/Role/Scope mapping path
- Oracle/provider/connectivity path
- P1 physical authority package or equivalent production authority implementation
- TLS test path
- test backup/restore ownership

## Vertical Slice Stages

| مرحله | Actor/Role اصلی | Server Authority مورد انتظار | Evidence اجباری |
|---|---|---|---|
| 1. Source/Intake | Observer / Intake Steward | Identity + Role/Scope + create/route command | Created ID + Audit |
| 2. G01 | Intake Steward | State/version/rule validation | Decision snapshot + event |
| 3. Case/G02 | Case Reviewer | Atomic decision + Need creation when qualified | Case decision + Need ID + Audit/Outbox |
| 4. Need/G03 | Need Owner + Need Reviewer | submit + independent review | version history + SoD evidence |
| 5. Idea | Idea Owner | exact Need linkage + version control | Idea ID/version |
| 6. Dynamic Evaluation Plan | System | plan generated from frozen idea/version | plan snapshot |
| 7. Specialist Assessments | Assignment-bound assessors | exact Assignment/Idea/Role context | completed assignments + timestamps |
| 8. G04 vote | Committee members | unique vote/member against frozen snapshot | vote records |
| 9. G04 final decision | IDEA_DECISION | separate final authority | final decision + Audit |
| 10. Portfolio Eligibility | System | automatic after G04 Approved | eligibility result + reason |
| 11. Portfolio | Portfolio Manager | candidate→membership/priority/handoff control | portfolio decision records |
| 12. Execution | Execution Owner | approved baseline reference + progress commands | plan/progress evidence |
| 13. Completion | Execution Owner + independent reviewer where required | submit vs review separation | completion dossier + review |
| 14. Benefit | Benefit Owner + Verifier | baseline/target/measurement/verification | benefit evidence |
| 15. Knowledge | Author/Steward/Publisher | validation/publication separation | knowledge asset + status history |

## Mandatory Cross-Cutting Proofs

### Identity & Authorization
- real Windows user reaches server
- Windows Identity maps to canonical PersonID
- RoleAssignment + Scope validated server-side
- unauthorized direct API command is rejected without mutation
- client-supplied identity headers are never trusted as authority

### Concurrency
For at least one mutable aggregate:
- request A succeeds with `expectedVersion=N`
- request B using stale `expectedVersion=N` is rejected
- no lost update occurs

### Idempotency
For at least one state-changing command:
- same `idempotencyKey` replayed
- side effect occurs once only
- replay returns deterministic prior result/contract behavior

### Atomicity
For at least one transition:
- State mutation + Audit + Outbox are committed together
- forced transaction failure produces none of the three as partial success

### Audit
Audit record must include at minimum:
- PersonID
- effective Role
- Assignment when applicable
- IdentitySource
- Entity/Aggregate ID
- entity version
- RuleSet/version when applicable
- timestamp
- correlation identifier

## G04 Rules

- Committee vote and final decision remain separate authorities.
- Dynamic Evaluation Plan is the truth source for required assessments.
- A stale assessment/idea version must not silently pass.
- final decision operates against a frozen snapshot.

## Portfolio Rules

- `PortfolioEligibilityService` remains in architecture.
- For normal users it runs automatically after `G04 Approved`.
- Manual invocation is reserved for Admin/Test/UAT only.
- Candidate, Membership and Execution Handoff remain distinct domain concepts even if UX combines actions.

## Assignment-Bound Work

Role-sensitive assessment pages must open from a valid assignment context:

`User → My Work Assignment → Entity/Idea → Role → Allowed Action`

Direct/stale/fallback links without valid assignment must fail closed.

This applies explicitly to Structured Assessment and specialist evaluation workspaces.

## Execution Rules

EIMS is not required to become a full PMIS.

Minimum execution data for the slice:
- owner
- approved scope/baseline
- key deliverable
- acceptance criteria
- target dates
- high-level budget/resource reference
- key risk/milestone evidence
- completion dossier

## Benefit Rules

Minimum proof:
- KPI/benefit definition
- baseline
- target
- data source
- measured result
- evidence
- independent verification when required

Attribution may remain conditional depending on case complexity, but no realized benefit may be asserted without measurement evidence.

## Knowledge Rules

- knowledge authoring, validation and publication are distinct responsibilities
- publication must be auditable
- AI may assist drafting/search but cannot publish or approve autonomously

## Test Case Selection

First case should be controlled and medium-complexity:
- one real business need
- one idea
- at least one specialist assessment
- G04 committee path
- one portfolio assignment
- one execution
- one measurable benefit
- one knowledge asset

After PASS, repeat with:
1. Simple case
2. Complex multi-assessment case

## Exit Criteria

OP-05 is PASS only when:

1. full path executes on real authenticated/persistent environment;
2. all stage IDs are traceable end-to-end;
3. concurrency/idempotency/atomicity tests PASS;
4. unauthorized actions are rejected;
5. no stage relies on client-side authority;
6. Audit/Outbox evidence exists;
7. PortfolioEligibilityService runs automatically after G04 Approved;
8. user can complete the path without administrative/test-only shortcuts;
9. repeat run is deterministic enough for regression automation;
10. no frozen v6.360 invariant was changed without ACR.

## Stop Conditions

Stop and classify before changing code if:
- real environment contradicts an architecture assumption;
- workflow mismatch is discovered between backend and v6.360;
- a customer-specific requirement would create a core fork;
- security requires trusting client-controlled identity/state;
- a production secret would need to be stored in source/config repository;
- a business-rule change is proposed during defect fixing.
