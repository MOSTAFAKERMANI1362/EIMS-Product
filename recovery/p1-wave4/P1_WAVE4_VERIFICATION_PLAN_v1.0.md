# P1 Recovery Wave 4 — Verification Plan v1.0

## Scope

This plan verifies the logical promotion of exactly two G03 commands after ACR-P0-003, without activating the P5 command gateway and without claiming physical Oracle or Windows Domain readiness.

Promoted commands:
- `needs.submit-g03`
- `needs.g03-decision`

## Mandatory gates

1. `RecoveredApiCommandCatalog` marks mutation recovered only for the two G03 commands.
2. `RecoveredG03MutationPlanner` computes server-authoritative State, G03 review status, workflow routing and event identity.
3. Owner PersonID, owner role and scope are preserved by the G03 mutation planner.
4. `workRoutingRole` remains a workflow-routing fact only and has no RBAC/identity side effect.
5. APPROVE and RETURN create immutable `G03ReviewDecision` envelopes; submit creates no reviewer decision.
6. Authority context for decisions is stamped by `AuthorityKernel`, not accepted from the request body.
7. RETURN note remains in authoritative decision data and is not copied to the integration-event identity/payload surface represented by the current outbox envelope.
8. P2 commits Aggregate State + Decision(s) + Audit + Outbox + Idempotency atomically, with rollback at each injectable staging point.
9. Optimistic concurrency and exact idempotent replay remain enforced.
10. G03 Need Owner self-review remains blocked by SoD.
11. Non-G03 Product commands remain fail-closed at their earliest unrecovered contract gate.
12. P5 `FailClosedCommandGateway` remains unbound.
13. Frozen `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` remains unchanged.

## CI evidence required before merge

- Wave 4 G03 mutation suite: all PASS, 0 warnings, 0 errors.
- P1 Authority recovery suite: all PASS.
- P2 Persistence recovery suite: all PASS.
- P0→P1 layered recovery gate: all PASS.
- Existing G03 Rule Recovery suite: PASS.
- Existing G03 Event Recovery suite: PASS.
- P5/Pilot logical composition suites: PASS where triggered by touched contracts.
- Security Pipeline: repository policy + Trivy vulnerability/secret/misconfiguration scan + CycloneDX SBOM PASS.

## Non-claims

Passing this plan does not establish:
- physical Oracle binding,
- live Windows Domain authentication,
- real HR/Org activation,
- Network Pilot readiness,
- P5 domain-command activation,
- production or commercial release readiness.
