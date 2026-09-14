# EIMS Product

Enterprise Innovation Management System (EIMS) — controlled engineering repository.

## Repository authority

- `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` remains the frozen Executable Product Specification and must not be edited in-place.
- Commercial/product development proceeds through versioned packages, branches, pull requests, and change control.
- No production secrets, Oracle credentials, HR exports, customer personal data, or confidential operational datasets may be committed.

## Program tracks

1. **Reference Customer / Network Pilot** — Mes Shahid Bahonar
2. **Commercial Productization** — EIMS Commercial Release 1.0

## Engineering principles

- Server-authoritative backend
- Identity → PersonID → Role/Scope → Authorization → Domain Command
- Atomic State + Audit + Outbox
- Optimistic concurrency and idempotency
- Configuration over customer-specific core forks
- AI is optional/advisory and never owns final business authority

## Working model

- `main`: protected release/baseline branch
- `op-*`: controlled operations and governance changes
- `feature/*`: product features after approval
- `fix/*`: defect correction
- `security/*`: security remediations
- Changes should reach `main` through Pull Request review.

> Repository bootstrapped under OP-01 — Repository & Configuration Control.
