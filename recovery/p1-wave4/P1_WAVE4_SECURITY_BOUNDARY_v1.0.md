# P1 Recovery Wave 4 — Security Boundary v1.0

Wave 4 is a logical server-authority recovery increment. It does not change the frozen HTML product baseline and does not activate a live pilot command path.

Security invariants:

- Identity authority remains external to workflow routing. `WorkRoutingRole` is not a role assignment, claim, permission, or trust source.
- Authorization requires a resolved server-side actor with PersonID, Windows/network identity source, AssignmentID, role and scope.
- G03 mutation values are server-computed. Client-supplied `state`, `g03ReviewStatus`, `workRoutingRole`, event name, decision authority identity, timestamp or entity version are not authoritative inputs.
- Need Owner self-review remains denied by SoD.
- Decision authority fields are stamped by `AuthorityKernel` from the authenticated/resolved actor context.
- Persistence rejects decision envelopes whose aggregate/version, correlation or authority context do not match the command transaction.
- Decision history is append-only within the current logical store contract; duplicate decision IDs are rejected.
- Exact idempotent replay must not duplicate Aggregate mutation, decision record, audit or outbox evidence.
- P5 `FailClosedCommandGateway` remains unchanged and unbound.
- Physical Oracle and Windows Domain readiness remain separate evidence gates.

Any later P5 activation must be a separate controlled change with live identity, persistence and environment evidence.