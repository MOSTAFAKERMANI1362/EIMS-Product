# EIMS Repository Policy

## 1. Authority

This repository is the canonical engineering workspace for EIMS productization and pilot implementation.

The frozen `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` remains the Executable Product Specification. It is never modified in-place.

## 2. Branch policy

- `main`: approved baseline/release only
- `op-*`: technical-management operations
- `feature/*`: approved feature work
- `fix/*`: defect remediation
- `security/*`: security remediation

All substantive changes should be reviewed through Pull Requests before reaching `main`.

## 3. Prohibited content

Do not commit:
- passwords, tokens, API keys, certificates or private keys
- Oracle connection strings or production credentials
- real HR exports or personal data
- confidential customer operational datasets
- production logs containing sensitive data

Use sanitized examples and configuration templates instead.

## 4. Change control

A change affecting a frozen product invariant, G01-G04 behavior, Dynamic Evaluation Plan, SoD, Digital Thread, audit semantics, or final decision authority requires formal change review/ACR before implementation.

## 5. Definition of evidence

AI output, prose, or source code alone is not verification evidence. Evidence is produced by repeatable tests, build results, security scans, logs, signed decisions, or approved review records.
