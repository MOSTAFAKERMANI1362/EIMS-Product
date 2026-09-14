# EIMS P3 Windows Identity/RBAC Recovery — Source Registry v1.0

**Status:** Logical runtime recovery; live Domain/IIS evidence pending  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Recovered authority chain

`Authenticated Windows Principal → NetworkAccount → ACTIVE PersonID → exact effective Assignment → server Role/Scope → AuthorityActor`

Windows Authentication proves who the user is. It does not itself decide what the user may do inside EIMS. Role and Scope are resolved from EIMS-controlled assignments on the server.

## Trust boundary

- IIS Windows Authentication is the intended Pilot hosting mode.
- Anonymous access is denied.
- Client identity/role/scope headers are untrusted.
- `DOMAIN\\user` lookup is case-insensitive and must resolve to exactly one active PersonID.
- Every mutation context requires a specific AssignmentId.
- Assignment must belong to that PersonID, be currently effective, and not be revoked.
- AuthorityActor contains only the Role/Scopes of the selected assignment. Roles from other assignments are never silently unioned.

This exact-assignment rule is intentional: it prevents a user with multiple legitimate responsibilities from accidentally exercising a stronger authority in the wrong case/context.

## P4 boundary

P4 may provide approved Person/Organization/NetworkAccount directory data, but it never creates EIMS Role or Scope authority. Role assignment administration is a separate controlled and audited capability.

## Live evidence not yet claimed

The logical resolver can be built and tested without access to the corporate Domain. This does not close `p3.windows.live`. Network Pilot still requires a real domain-joined Pilot host where:

1. IIS Windows Authentication yields a real authenticated DOMAIN account;
2. the account resolves uniquely to an ACTIVE PersonID;
3. a real effective Assignment is resolved server-side;
4. Role/Scope are loaded from that Assignment;
5. forged client identity/role/scope headers do not alter the outcome.

No such live evidence is fabricated in this package.
