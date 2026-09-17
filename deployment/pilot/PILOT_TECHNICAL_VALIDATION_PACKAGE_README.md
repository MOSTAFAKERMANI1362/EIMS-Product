# EIMS Pilot Technical Validation Package

Status: PRE-PILOT / FAIL-CLOSED / ISOLATED TECHNICAL VALIDATION

## Purpose

This package is intended for controlled review and isolated technical validation by IT. It is not a production Go-Live installer and does not authorize connection to operational ERP/HR schemas.

## Why ZIP instead of MSI

For the pre-Pilot stage, the package is deliberately a transparent ZIP rather than an MSI/Setup executable. IT can inspect every file, verify SHA-256 hashes, copy the application into an isolated VM, and remove it by deleting the application directory and IIS site/application. No registry mutation or hidden installer action is required by this package.

## Package contents

- `app/` — framework-dependent published ASP.NET Core Pilot Host.
- `docs/` — OP-04 template, physical readiness runbook, responsibility matrix, production composition binding plan, deployment/rollback checklist, and a non-secret configuration example.
- `PACKAGE_METADATA.json` — package purpose, build SDK, source commit when available, and fail-closed status.
- `SHA256SUMS.txt` — SHA-256 for packaged files.
- `READ_FIRST.txt` — short security and deployment notice.

## Prerequisites controlled by IT

IT should provide an isolated Windows Server VM for validation and independently approve/install:

1. IIS.
2. Windows Authentication feature.
3. .NET 10 ASP.NET Core Hosting Bundle/runtime prerequisites approved by IT.
4. A dedicated IIS site/application pool for EIMS Pilot.
5. A controlled hostname/TLS binding when IT is ready to test HTTPS.

Do not use an operational ERP/HR database account for initial technical validation.

## Initial installation mode

The recommended first deployment is **application-only / fail-closed**:

1. Extract the ZIP into an IT-controlled staging directory.
2. Verify `SHA256SUMS.txt` and the ZIP `.sha256` file.
3. Inspect `PACKAGE_METADATA.json`.
4. Copy `app/` to the IT-approved application directory, for example `C:\inetpub\EIMS-Pilot`.
5. Create a dedicated IIS application pool. Use `No Managed Code` for the application pool.
6. Create a dedicated IIS site/application pointing only to the EIMS Pilot folder.
7. Enable Windows Authentication and disable Anonymous Authentication for the EIMS application when the Domain test begins.
8. Do not add Oracle credentials or an OP-04 PASS file at this stage.
9. Confirm `/health` reports a fail-closed activation state.

## Database safety boundary

For the first validation stage, EIMS should have **no access to operational ERP/HR schemas**. When DBA validation begins, use a dedicated EIMS schema/service identity under least privilege. The runtime identity must not receive DBA, `CREATE ANY`, `DROP ANY`, or cross-schema write authority.

Passwords, connection strings, Oracle wallets, tokens and private certificate material must remain in IT-approved secret storage and must never be placed in Git, package documentation, chat or OP-04 evidence.

## Activation boundary

The Host remains fail-closed until both conditions are true:

1. authoritative `PILOT_ENVIRONMENT_EVIDENCE` passes all nine OP-04 gates; and
2. the real `IProductionRuntimeComposition` reports validated durable P2, authoritative P3 and a non-fail-closed candidate gateway.

Configuration flags alone cannot authorize activation.

## Rollback

Before production composition is enabled, rollback is simply:

1. stop/remove the EIMS IIS site/application;
2. remove the EIMS application directory after preserving approved logs/evidence;
3. no ERP/HR data rollback should be required because initial technical validation must not use those schemas.

Once the dedicated EIMS Oracle schema is introduced, rollback and restore must follow the DBA-approved backup/restore plan in OP-04/OP-05.

## MSI / commercial installer

A signed/managed MSI or enterprise installer is intentionally deferred until after the physical Pilot and OP-05 End-to-End PASS. Productization will then add installer lifecycle, upgrade, repair/uninstall, configuration management and support packaging.
