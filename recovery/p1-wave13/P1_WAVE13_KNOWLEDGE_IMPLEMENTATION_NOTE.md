# P1 Wave13 — Knowledge Runtime

Status: IMPLEMENTATION CANDIDATE — LOCAL VALIDATION REQUIRED

Wave13 implements the three Knowledge mutations stabilized by ACR-P0-008 without activating the Pilot Host or adding a P1→P5 Knowledge binding.

## Commands
- `knowledge.create-draft`
- `knowledge.validate`
- `knowledge.publish`

## State machine
`DRAFT → VALIDATED → PUBLISHED`, with `RETURN` routes back to `DRAFT`.

## Authority and evidence
- Draft creation requires authoritative server-side Benefit evidence with `Benefit.status=REALIZED`.
- Source Benefit version is matched to `expectedVersion` for create-draft.
- verification, attribution and realization dossiers are mandatory source evidence.
- Knowledge author authority is resolved by server-side author policy.
- default policy family is `NEED_OWNER_OR_DOMAIN_EXPERT`; supported recovered author-role alternatives are NEED_OWNER, IDEA_OWNER and DOMAIN_EXPERT.
- Benefit Owner alone does not grant Knowledge author authority.
- `KNOWLEDGE_STEWARD` authority validates.
- `KNOWLEDGE_PUBLISHER` authority publishes.
- validation and publication authorities remain distinct at the server-resolved role/assignment boundary; no stronger person-level prohibition is invented beyond ACR-P0-008.
- publication requires a publication dossier.

## Persistence
Provider-neutral reference persistence proves optimistic concurrency, replay-safe idempotency, atomic state+audit+outbox+idempotency and rollback under injected faults. It is not an Oracle adapter or OP-04 evidence.

## Non-claims
- no Knowledge P1→P5 binding in this wave
- no physical Oracle binding
- no live Domain/IIS activation
- no Network Pilot readiness claim
- frozen v6.360 unchanged
