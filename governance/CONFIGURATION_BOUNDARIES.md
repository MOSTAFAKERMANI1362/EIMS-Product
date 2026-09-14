# EIMS Configuration Boundaries

This document separates what may be configured per customer from what is a frozen product invariant.

## Product invariant — change control required
- Digital Thread semantics
- canonical entity identity/history
- server authority
- separation of duties
- audit append-only behavior
- G01..G04 approved workflow semantics
- Dynamic Evaluation Plan truth source
- final decision authority model
- state/audit/outbox atomicity
- optimistic concurrency and idempotency

## Customer configuration — allowed within governed ranges
- organization structure mapping
- role assignments and scopes
- portfolio definitions
- approved labels/help texts
- thresholds/weights only where explicitly declared configurable
- notification settings
- branding
- integration endpoint bindings

## Environment configuration
- database host/provider settings
- identity provider bindings
- TLS endpoints
- logging/monitoring endpoints
- storage paths

## Secrets — never committed
- passwords
- API keys
- certificates/private keys
- service-account secrets
- production connection strings

## Feature flags
Feature flags may control rollout or optional modules but must not silently change a frozen business invariant.
