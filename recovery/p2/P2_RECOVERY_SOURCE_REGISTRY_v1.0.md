# EIMS P2 Persistence Recovery — Source Registry v1.0

**Status:** RECOVERY ARTIFACT / NOT ORIGINAL P2 IMPLEMENTATION  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Purpose

This package restores the logical persistence contract required by the recovered P1 Backend Authority. It deliberately does not select an Oracle client/provider, define production DDL, or include connection credentials because those decisions require real environment evidence.

## Authoritative requirements recovered

- every mutation is server-authoritative;
- optimistic concurrency is mandatory;
- idempotency is enforced server-side;
- Aggregate State + Append-only Audit + Event Outbox + Idempotency Record are one atomic transaction unit;
- exact idempotent replay returns the prior result without a second mutation;
- re-use of an idempotency key with a different fingerprint is rejected;
- Audit retains PersonID, network identity, identity source, role set, assignment, entity version, rule set, timestamp, correlation and command identity;
- Event Outbox retains aggregate version and correlation;
- any write failure rolls the whole transaction back.

## Oracle binding status

Physical Oracle binding remains **BLOCKED** until all of the following are evidenced from the organization environment:

1. Oracle database version;
2. approved .NET Oracle provider and version;
3. approved connectivity mode;
4. service-account model;
5. schema owner;
6. evidence reference / environment approval.

No password, token, secret or production connection string belongs in this recovery contract or source repository.

## Adapter boundary

P1 continues to depend on `IAuthorityStore`. `TransactionalAuthorityStore` is a dependency-free transactional reference implementation used to prove contract semantics. The later production Oracle adapter must implement the same port and pass the same contract/fault tests.

## Physical decisions intentionally deferred

- table/sequence/index names;
- Oracle-specific SQL and locking syntax;
- tablespaces/storage configuration;
- provider package/version;
- client mode;
- migration tooling;
- actual connection strings and credentials.

These are not blockers for proving P2 logical correctness, but they are blockers for `p2.oracle.live` and Network Pilot.

## Evidence gates

The P2 package is acceptable only when:

- .NET SDK 10.0.302 build succeeds with zero errors;
- all P2 contract tests pass;
- fault injection proves rollback after State, Audit, Outbox and Idempotency write points;
- concurrent stale-version writers produce exactly one winner;
- concurrent identical idempotency keys produce one commit plus one replay;
- the machine-readable recovery verifier confirms physical Oracle status remains blocked rather than fabricated;
- Security Pipeline passes.
