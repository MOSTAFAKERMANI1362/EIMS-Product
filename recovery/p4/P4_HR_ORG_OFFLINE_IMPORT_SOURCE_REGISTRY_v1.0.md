# EIMS P4 — HR/Organization Offline Import Source Registry v1.0

**Status:** Pilot implementation contract  
**Source HR:** Oracle HR, initially via controlled offline export  
**Network identity target:** Windows Domain account → PersonID → Role/Scope in P3  
**Production Oracle persistence:** deferred until P2 physical Oracle binding is evidenced

## Design intent

P4 establishes a narrow, controlled bridge from HR/organization data into the EIMS identity directory. It is not an HR master-data replacement and it is not an RBAC engine.

The Pilot canonical exchange is UTF-8 CSV because it is deterministic, dependency-free and easy to generate from Oracle or Excel. Excel files are therefore treated as source documents that must be converted to the canonical schema before ingestion; direct spreadsheet parsing is not part of the Pilot trust boundary.

## Canonical data set

Only the minimum fields needed by EIMS identity/org context are accepted:

- PersonId
- EmployeeNumber
- NetworkAccount
- DisplayName
- OrgUnitCode
- OrgUnitName
- ManagerPersonId
- EmploymentStatus
- EffectiveFrom

National ID, salary, personal contact information, date of birth, bank data and health data are outside this contract.

## Identity and access-control boundary

P4 can create or update Person, Organization and NetworkAccount binding candidates. It must never:

- grant an EIMS Role;
- grant an EIMS Scope;
- infer privileges from Job Title;
- infer privileges from Org Unit;
- replace P3 Role Assignment authority.

A unique, active PersonID and unique Windows account binding are prerequisites for P3 authentication/authorization, but they are not authorization by themselves.

## Safety semantics

- Import is two-phase: validate/reconcile first, explicit approval/activation later.
- File upload alone never mutates the active directory.
- Missing persons in a full snapshot are not automatically deactivated.
- Deactivation requires explicit `INACTIVE` evidence.
- Reactivation requires explicit `ACTIVE` evidence and approval.
- Persons are never hard-deleted because historical Audit references must remain resolvable.
- Rebinding one NetworkAccount or EmployeeNumber to another PersonID is blocked.
- Manager hierarchy must be resolvable and acyclic.
- Batch evidence includes schema version and SHA-256.

## Current implementation boundary

The implemented package provides:

1. dependency-free RFC4180-style CSV parsing;
2. semantic validation;
3. reconciliation planning;
4. non-sensitive CLI summaries for `validate` and `plan`;
5. synthetic contract tests.

Activation into Oracle is intentionally not implemented until the P2 physical Oracle adapter is approved and available. This is a real environment blocker, not an application-design blocker.
