# ACR-P0-006 — G04 Committee Voting and Frozen Governance Snapshot

**Status:** APPROVED_FOR_P1_IMPLEMENTATION  
**Decision class:** POST_FREEZE_ARCHITECTURE_DECISION  
**Frozen Product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` — unchanged

## Context

The frozen Product separates committee voting from the final G04 decision. A committee record freezes the approved governance profile and member composition, and each authenticated committee member casts an individual `APPROVE` or `REJECT` vote with an auditable reason/reference.

The frozen Product also supports configurable `MAJORITY`, `CONSENSUS`, and `CHAIR_TIEBREAK` rules. Quorum, rule, chair, governance profile and membership are case snapshots and must not drift with later configuration changes.

## Decision

`g04.vote` is a server-authoritative command for one exact PENDING `G04Assessment` on the `G04_COMMITTEE` route. The authenticated P3 Person and authority assignment must resolve to a member of the frozen committee snapshot. Client-selected member identity is never accepted.

Vote values are exactly `APPROVE` and `REJECT`. The trimmed vote reason/reference is required with minimum length 10, matching the final v6.349 layer in the frozen Product.

After quorum, approval is evaluated from the latest effective vote of each frozen member: majority uses `APPROVE > REJECT`; consensus requires all effective votes to be APPROVE; chair-tiebreak uses the chair's effective vote only when APPROVE and REJECT are tied, otherwise normal majority applies.

The voting stage completes when the approval rule succeeds, or—if no approving result exists—when every frozen member has voted after quorum. Stage completion ends committee action but does **not** change the Idea to APPROVED/REJECTED. The G04Assessment remains pending for the separate `IDEA_DECISION` authority.

## Vote correction and audit

The frozen UI replaces the prior effective vote for the same member. Production preserves that user-visible semantic without erasing history: before stage completion, a correction is a new append-only vote revision that supersedes the previous effective revision. Only the latest effective revision counts. Once the stage is complete no further vote/revision is accepted.

## Events

Two new post-freeze Domain Event identities are stabilized:
- `G04CommitteeVoteRecorded.v1`
- `G04CommitteeVotingCompleted.v1`

The vote note and full governance/member snapshots remain authoritative evidence and are not duplicated into integration-event payloads.

## Runtime gate

This ACR alone does not make `g04.vote` executable. Runtime promotion additionally requires server governance-profile sourcing, committee membership resolution, immutable snapshot persistence, vote-rule evaluation, append-only vote storage, P3 identity/role/scope enforcement, optimistic concurrency, idempotency, atomic persistence and regression/security evidence.

`g04.final-decision`, P5 binding, physical Oracle readiness, live Windows Domain evidence and Network Pilot readiness are explicitly outside this ACR.
