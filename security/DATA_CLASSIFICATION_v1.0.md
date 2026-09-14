# EIMS Data Classification v1.0

**Status:** Baseline for OP-02

## 1. Classification levels

### PUBLIC
Information approved for public release.
Examples: public product brochure, public documentation, published security contact.

### INTERNAL
Non-public operational/product information whose disclosure causes limited impact.
Examples: internal process notes, non-sensitive configuration guidance, sanitized test data.

### CONFIDENTIAL
Business, customer or employee information whose unauthorized disclosure can create material operational, contractual or privacy impact.
Examples: real EIMS cases, idea content, project evidence, HR/person data, organization mappings, customer configuration, audit reports, internal knowledge assets.

### RESTRICTED
Security-sensitive or high-impact information requiring the strongest controls.
Examples: credentials, production connection strings, private keys, service-account secrets, privileged security evidence, incident artifacts containing exploitable detail.

## 2. Default classification by EIMS data domain

| Data domain | Default | Notes |
|---|---|---|
| Product source code | INTERNAL | May be more restrictive by commercial policy |
| Frozen product specification | INTERNAL | Customer distribution only under approved terms |
| PersonID / employee mapping | CONFIDENTIAL | Personal/organizational data |
| RoleAssignment / Scope | CONFIDENTIAL | Also security-sensitive |
| ImprovementSource / Case / Need / Idea | CONFIDENTIAL | May contain operational IP |
| G01–G04 decisions/votes | CONFIDENTIAL | Governance evidence |
| Portfolio/Execution/Benefit | CONFIDENTIAL | Commercial/financial/operational information |
| Attachments | CONFIDENTIAL by default | Elevate based on content |
| Knowledge assets | CONFIDENTIAL until explicitly published | Publication is an explicit action |
| Audit logs | CONFIDENTIAL | Security/behavioral evidence |
| Security logs with attack detail | RESTRICTED | Access limited to authorized security/support roles |
| Passwords/API keys/private keys | RESTRICTED | Never stored in Git |
| Production DB connection strings | RESTRICTED | Secret store only |
| Sanitized synthetic test data | INTERNAL | Must not be reversible to real persons/customers |
| SBOM | INTERNAL by default | Customer disclosure policy may permit sharing |
| Security whitepaper | PUBLIC or customer-shareable | Release approval required |
| PenTest full report | RESTRICTED | Executive summary may be CONFIDENTIAL |
| AI prompt/context containing enterprise records | Same as highest source data | No classification downgrade through AI |

## 3. Handling rules

### PUBLIC
- May be stored and shared openly after approval.

### INTERNAL
- Authenticated staff/contributors only.
- May be used in approved engineering SaaS tools when no customer-confidential data is embedded.

### CONFIDENTIAL
- Least-privilege access.
- Encryption in transit; encryption at rest where platform supports it.
- No use in external AI/SaaS without explicit organization/customer authorization and data-processing review.
- Production/customer samples must not be committed to source control.
- Support bundles must redact unnecessary personal/security fields.

### RESTRICTED
- Never commit to Git.
- Never paste into AI prompts or issue trackers.
- Store in approved secret/security repository or platform secret store.
- Access must be logged and tightly scoped.
- Rotation/revocation procedure required for credentials.

## 4. Privacy and retention principles

- Collect only data required for a defined product/business purpose.
- Person/role data has validity periods and must support deactivation without destroying historical accountability.
- Historical decisions/audit are retained according to policy; terminal records are archived/read-only rather than silently deleted.
- Retention duration is customer/legal policy and must remain configurable without compromising required audit evidence.
- Exports must preserve authorization and classification controls.

## 5. AI-specific handling

- AI does not lower classification.
- Retrieval must be authorized using current PersonID/Role/Scope before context reaches the model.
- Prompt logs must avoid storing unnecessary CONFIDENTIAL/RESTRICTED data.
- External model providers are disabled by default for customer-sensitive deployments unless explicitly approved.
- Local AI is preferred where customer policy prohibits data egress.

## 6. Repository rule

Only PUBLIC, INTERNAL, sanitized synthetic examples and approved non-sensitive templates belong in the product repository. Real HR exports, customer records, Oracle dumps, secrets and production diagnostic data are prohibited.
