# P1 Recovery Wave 4 — G03 Mutation Binding

Status: implementation candidate for controlled merge.

This wave consumes approved ACR-P0-003 and promotes only `needs.submit-g03` and `needs.g03-decision` through the mutation-contract gate.

Implemented logical path:

`Server identity/assignment → state gate → SoD → recovered G03 rules → recovered G03 mutation planner → server-stamped decision envelope → transactional persistence → Audit + Outbox + Idempotency`

Key boundaries:

- `WorkRoutingRole` is a workflow-routing fact only. It is not an RBAC grant, Windows claim, Person mapping, or assignment.
- Owner PersonID, OwnerRole and Scope are preserved by G03 mutations.
- G03 APPROVE/RETURN decisions are immutable domain decision envelopes stamped by `AuthorityKernel` with PersonID, AssignmentID, entity version, timestamp and correlation ID.
- RETURN note remains authoritative decision data; integration Outbox contains only the approved event identity/envelope metadata.
- Transactional logical persistence stages Aggregate State + Domain Decision(s) + Audit + Outbox + Idempotency and rolls all of them back together on injected faults.
- Exact idempotent replay does not duplicate decisions, audit, outbox, or state changes.
- Non-G03 Product commands remain at their prior fail-closed recovery gates.
- P5 `FailClosedCommandGateway` remains unchanged and unbound.
- This wave does not claim physical Oracle readiness, live Windows Domain identity, Network Pilot readiness, or Production Go-Live readiness.
- Frozen `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` is unchanged.
