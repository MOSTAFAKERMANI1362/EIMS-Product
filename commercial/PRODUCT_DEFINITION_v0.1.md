# EIMS Commercial Product Definition v0.1

**Status:** Controlled Draft — technical baseline, not a pricing/legal commitment  
**Target:** EIMS Commercial Release 1.0

## 1. Product statement

EIMS is an enterprise innovation and improvement management platform that manages the governed digital thread:

`ImprovementSource → Case → Need → Idea → Portfolio → Execution → Benefit → Knowledge`

It is designed for organizations that need traceable innovation/improvement governance, role-based decisions, evidence-backed execution, realized-benefit tracking and reusable organizational knowledge.

## 2. Commercial design goal

A new organization must be deployable without a customer-specific fork of the EIMS Core.

Differences between customers should be handled through:
- organization/person/role mapping;
- approved customer configuration;
- integration adapters;
- branding/help content;
- explicitly configurable rules/thresholds;
- environment bindings.

Frozen product invariants remain under Change Control.

## 3. Release 1.0 deployment model

### Baseline
**On-Premise / Single-Tenant per customer**

Rationale:
- lower data-residency and customer-isolation risk for first commercial release;
- natural fit for organizations using Windows Domain and internal databases;
- simpler backup/restore and support boundary;
- avoids premature SaaS multi-tenancy complexity.

### Future-compatible constraint
The core model remains Organization/Tenant-aware so future hosted/SaaS deployment is not architecturally blocked.

## 4. Target customer profile — initial technical hypothesis

Primary fit:
- medium and large organizations;
- industrial/manufacturing organizations;
- organizations with formal R&D, innovation, continuous-improvement or suggestion governance;
- organizations requiring committee/role-based decisions and auditable approvals;
- organizations that need to connect ideas to execution, measurable benefits and knowledge.

Commercial validation of market size, geography and buyer personas is outside this technical baseline and remains a business-validation activity.

## 5. Core product capabilities — Release 1.0

### Mandatory core
1. Improvement Source / Intake
2. Universal Case
3. Need Management
4. Idea Management
5. Dynamic Evaluation Plan
6. G01..G04 governed decisions
7. Portfolio intake / membership / handoff
8. Adaptive Execution governance
9. Benefit baseline / measurement / verification / realization
10. Knowledge asset lifecycle
11. Role/Scope authorization
12. Worklist / Assignment context
13. Append-only Audit
14. Event Outbox / integration boundary
15. Archive / history / traceability
16. Customer configuration management
17. Admin / diagnostics / health

### Optional or separately licensed-capable modules
- AI Assistant / enterprise RAG
- advanced analytics and management dashboards
- external ERP/HR integration adapters beyond baseline import
- reward/performance integrations
- additional notification channels
- advanced portfolio analytics

Optional modules must not be required for correctness of the core workflow.

## 6. Explicit non-goals for Commercial Release 1.0

EIMS 1.0 is **not** intended to be:
- a full ERP;
- the master HR system;
- a full PMIS replacing detailed project scheduling tools;
- a document-management system for arbitrary enterprise content;
- a fully autonomous AI decision maker;
- a public multi-tenant SaaS platform in the first release;
- a customer-specific codebase per organization.

## 7. Identity strategy

### Reference adapter
Windows Integrated Authentication / Active Directory for the reference pilot.

### Product boundary
Identity is adapter-based. Future supported adapters may include:
- LDAP/AD variants;
- Microsoft Entra ID;
- approved local identity provider;
- other enterprise identity providers.

All adapters must resolve to:

`External Identity → PersonID → RoleAssignment → Scope`

The backend remains the authorization authority.

## 8. Data / database strategy

- Oracle is the first reference enterprise persistence/integration environment.
- Oracle must not become an architectural dependency of the commercial core.
- persistence provider boundary remains explicit;
- schema migration/versioning is mandatory;
- at least one additional database-provider strategy must be evaluated before broad commercial rollout;
- selection of the second officially supported DB provider remains TBD pending market and engineering evidence.

## 9. AI strategy

AI is optional and provider-neutral.

AI may:
- explain fields/process;
- summarize approved context;
- draft content;
- suggest CandidatePatch changes;
- search authorized knowledge;
- assist analysis.

AI may not:
- own final business decisions;
- approve/reject sovereign workflow gates autonomously;
- bypass Role/Scope authorization;
- write unrestricted data directly to persistence;
- make core workflow availability dependent on model availability.

## 10. Product packaging requirements

Commercial Release 1.0 requires:
- versioned installer/deployment package;
- configuration templates;
- database migration package;
- upgrade and rollback procedure;
- admin guide;
- user guide;
- integration/API guide;
- security whitepaper;
- SBOM;
- release notes;
- diagnostic/support bundle;
- backup/restore runbook;
- vulnerability disclosure/handling process.

## 11. Commercial quality gates

Release is not authorized by feature completeness alone.

Required evidence includes:
- product regression;
- security pipeline;
- independent penetration test before commercial RC;
- performance/load evidence;
- backup/restore drill;
- real reference-customer pilot;
- second-organization portability pilot;
- installation/upgrade repeatability;
- support and vulnerability-response readiness.

## 12. Configuration vs customization rule

A customer request is accepted as configuration only when it stays inside an approved extension/configuration boundary.

A request that changes:
- Digital Thread semantics;
- SoD;
- security authority;
- frozen gate semantics;
- audit invariants;
- canonical identity/history;
- atomicity/concurrency/idempotency guarantees;

requires Product Change Review/ACR and must not be implemented as a hidden customer override.

## 13. Initial edition strategy

Do not create many editions before market evidence exists.

Recommended initial commercial structure:
- **EIMS Core** — mandatory governed innovation lifecycle;
- **EIMS AI** — optional AI/RAG capability;
- **EIMS Integration Pack** — optional/contracted adapters;
- **EIMS Analytics** — optional advanced analytics when mature.

Pricing, license metric and packaging names are business TBDs and are intentionally not fixed here.

## 14. Commercial Release 1.0 exit statement

EIMS becomes a commercial product only when:

1. the reference customer operates it in a real environment;
2. a second organization can deploy it without core-code fork;
3. installation, upgrade, security, backup and support are repeatable;
4. product ownership/licensing/legal rights are clear;
5. commercial support and vulnerability-response processes exist.
