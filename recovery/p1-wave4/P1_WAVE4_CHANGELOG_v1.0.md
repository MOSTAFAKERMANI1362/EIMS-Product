# P1 Recovery Wave 4 — Change Log v1.0

This recovery increment implements the approved G03 mutation contract without changing the frozen product baseline.

Implemented logical changes:

- added `WorkRoutingRole` to the authoritative aggregate snapshot as a workflow-routing fact distinct from `OwnerRole` and RBAC assignments;
- added `DecisionIntent` and server-stamped `DomainDecisionEnvelope` contracts;
- extended `MutationPlan` to carry decision intents;
- added `RecoveredG03MutationPlanner` for submit / approve / return;
- promoted mutation recovery only for `needs.submit-g03` and `needs.g03-decision`;
- extended P2 logical persistence with append-only decision storage and duplicate-decision protection;
- included decision records in the atomic rollback boundary;
- added a decision-staging fault injection point;
- added dedicated Wave 4 contract tests and CI;
- updated existing recovery gates for the Wave 4 layered model.

No P5 command-gateway activation, no live Oracle binding, no live Windows Domain identity claim and no v6.360 modification are part of this change.