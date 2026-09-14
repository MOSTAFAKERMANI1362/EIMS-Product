# EIMS Commercial Product Configuration Model v1.0

**Status:** OP-03 baseline

## 1. Objective

EIMS must support different organizations without changing the commercial core for routine customer differences.

Configuration is allowed only where it does not silently redefine a frozen product invariant.

## 2. Configuration layers

### Layer A — Product Invariants
Not customer-configurable. Change requires formal product change control / ACR when applicable.

Examples:
- Digital Thread semantics.
- Canonical identity/version-history rules.
- Server-authoritative command model.
- Audit append-only semantics.
- State + Audit + Outbox atomicity.
- Optimistic concurrency and idempotency requirements.
- Approved G01..G04 workflow semantics.
- Dynamic Evaluation Plan as the source of truth for required G04 assessments.
- Committee member vote vs final G04 authority separation.
- Separation-of-duties controls.
- AI has no sovereign final-decision authority.

### Layer B — Customer Configuration
May vary by organization within governed constraints.

Examples:
- organization identifier/name/branding.
- business units and mapping codes.
- role assignment mapping.
- portfolio definitions.
- notification preferences.
- approved terminology/help text where it does not alter semantics.
- thresholds/weights only if the relevant product rule explicitly declares them configurable.
- enabled optional modules/features.
- locale/calendar/display settings.

### Layer C — Environment Configuration
Deployment-specific, not business policy.

Examples:
- database provider/host/schema binding.
- identity-provider binding.
- TLS/listening endpoints.
- file/object storage binding.
- logging/monitoring endpoints.
- AI gateway/model endpoint.
- integration adapter endpoints.

### Layer D — Secrets
Never stored in customer configuration files committed to source control.

Examples:
- passwords.
- API tokens.
- private keys.
- production connection strings containing credentials.
- service-account secrets.

Use approved secret-store/environment mechanisms.

### Layer E — Feature Flags
Used for safe rollout or optional capabilities, not to bypass frozen security/business invariants.

A feature flag must have:
- stable identifier.
- owner.
- default value.
- intended lifetime.
- security impact classification.
- removal/normalization plan when temporary.

## 3. Configuration authority

Changing a configuration value does not automatically mean the actor is authorized to change it.

Administrative configuration must be:
- authenticated.
- authorized by role/scope.
- validated against schema and governed ranges.
- versioned.
- auditable.
- reversible when technically possible.

## 4. Configuration lifecycle

`Draft → Validate → Review/Approve when required → Activate → Audit → Supersede`

The product must retain enough history to determine which configuration/rule version governed a historical decision.

## 5. Customer portability rule

Commercial Release 1.0 must demonstrate that a second organization can be onboarded without a customer-specific core-code fork.

Allowed:
- new customer config.
- new organization/role mappings.
- supported adapter binding.
- branding/help/terminology changes.

Not allowed without product change review:
- editing command authorization logic for one customer.
- special-case state transitions hardcoded by customer name.
- bypassing SoD.
- altering audit/history semantics.
- customer-specific direct database writes outside approved adapters.

## 6. Configuration validation

Before activation, configuration must pass:
1. JSON/schema validation where applicable.
2. invariant checks.
3. referential integrity checks.
4. security policy checks.
5. compatibility/version checks.
6. customer-configuration regression tests.

## 7. Versioning

Each configuration package should contain at minimum:
- `configVersion`
- `productCompatibility`
- `organizationId`
- `effectiveFrom`
- `createdBy/changeReference`
- checksum/hash for deployable configuration bundles.

## 8. Commercial rule

If a customer request cannot be expressed within this model, it must be classified as one of:

1. Product defect.
2. Product feature/change request.
3. New supported adapter.
4. Unsupported customization request.

It must not be implemented as an undocumented customer-specific fork.
