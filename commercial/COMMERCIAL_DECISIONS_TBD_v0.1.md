# EIMS Commercial Decisions — TBD Register v0.1

This register separates technical decisions already controlled by the product architecture from business/legal decisions that require explicit owner approval.

## A. Decisions that must be closed before Commercial Release Candidate

| ID | Decision | Why it matters | Technical Lead recommendation | Status |
|---|---|---|---|---|
| COM-LEGAL-001 | Product/IP ownership and right to commercialize | EIMS has been developed in an organizational/work context; rights must be unambiguous before sale/licensing | Obtain professional legal review and written ownership/right-to-commercialize position before external sale | OPEN |
| COM-BIZ-001 | Legal entity that will license/support EIMS | Contracts, invoicing, liability, tax and support responsibility | One clearly accountable legal entity | OPEN |
| COM-LIC-001 | License metric | Affects enforcement, pricing and customer procurement | Prefer simple organization/server/user-band model initially; avoid complex metering in v1 | OPEN |
| COM-PRICE-001 | Pricing | Commercial strategy, not architecture | Do not hard-code pricing/licensing logic until market validation | OPEN |
| COM-SLA-001 | Support hours and SLA | Drives architecture/operations staffing | Define Standard support first; premium SLA later if demanded | OPEN |
| COM-GEO-001 | Initial sales geography/market | Determines compliance, language, contracts and support | Start with a narrow target market and expand after reference deployments | OPEN |
| COM-BRAND-001 | Product name/trademark/branding | Packaging and legal identity | Validate name/trademark before Commercial RC | OPEN |
| COM-DATA-001 | Data processing/privacy contractual position | EIMS processes employee/organizational data | Prepare privacy/data-processing terms and retention responsibilities | OPEN |
| COM-CERT-001 | Certification target per customer segment | Determines cost/timeline | Do not pursue every certificate; use procurement-driven roadmap | OPEN |

## B. Technical decisions already baselined

| Decision | Status |
|---|---|
| v6.360 remains frozen Executable Product Specification | BASELINED |
| Commercial v1 starts On-Premise / Single-Tenant | BASELINED DIRECTION |
| Core remains server-authoritative | BASELINED |
| Oracle is an adapter/reference environment, not permanent commercial dependency | BASELINED DIRECTION |
| AI is optional/advisory and cannot own final business authority | BASELINED |
| Customer-specific core forks are prohibited by default | BASELINED |
| Second-organization pilot is mandatory before Commercial Release 1.0 | BASELINED |

## C. Decision timing

These business/legal questions do **not** block current product engineering, Network Pilot, security automation or architecture work.

They become release blockers at the following points:
- external sales/marketing commitment;
- paid customer pilot;
- license issuance;
- Commercial Release Candidate;
- certification application involving ownership/producer identity.

## D. Governance rule

The Technical Lead may propose and prepare options, but must not silently decide:
- legal ownership;
- contractual liability;
- pricing;
- binding SLA;
- certification expenditure;
- external paid commitments.
