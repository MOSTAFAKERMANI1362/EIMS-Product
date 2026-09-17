# EIMS Pilot Deployment and Rollback Checklist

## A. Pre-deployment approval

- [ ] Pilot VM/server is isolated from operational ERP/HR writes.
- [ ] Change/ticket reference exists.
- [ ] IT owner and DBA owner identified.
- [ ] Package ZIP SHA-256 matches the provided `.sha256` file.
- [ ] All package files match `SHA256SUMS.txt`.
- [ ] Package metadata states `ISOLATED_TECHNICAL_VALIDATION_ONLY`.
- [ ] No credentials, connection strings, tokens, PFX/private keys or HR/customer PII are present in the package.

## B. Windows/IIS baseline

- [ ] Windows Server/VM version recorded.
- [ ] Domain join completed if Windows Authentication validation is in scope.
- [ ] IIS installed.
- [ ] Windows Authentication feature installed.
- [ ] Approved .NET 10 ASP.NET Core Hosting Bundle/runtime prerequisites installed.
- [ ] Dedicated EIMS application directory created.
- [ ] Dedicated IIS application pool created; no unrelated applications share the pool.
- [ ] Dedicated IIS site/application created.
- [ ] Anonymous Authentication disabled when domain validation begins.
- [ ] Windows Authentication enabled when domain validation begins.

## C. Initial fail-closed validation

- [ ] EIMS starts without Oracle credentials.
- [ ] `/health` is reachable only through the approved Pilot endpoint.
- [ ] Runtime gateway remains fail-closed.
- [ ] No operational Oracle schema is reachable from the EIMS runtime identity.
- [ ] No HR export has been copied into the application directory.

## D. Oracle stage — only after DBA approval

- [ ] Dedicated EIMS schema identified/created.
- [ ] Dedicated runtime service identity created under least privilege.
- [ ] Runtime identity is not DBA.
- [ ] Runtime identity has no `CREATE ANY` / `DROP ANY` privileges.
- [ ] Runtime identity has no write access to ERP/HR schemas.
- [ ] Credentials are stored only in IT-approved secret storage.
- [ ] Live connectivity test recorded as an internal evidence reference.
- [ ] Backup exists before first physical persistence test.

## E. Identity/TLS/operations

- [ ] `DOMAIN\\user` principal validated through IIS Windows Authentication.
- [ ] Principal -> PersonID -> exact Assignment -> Role/Scope tested server-side.
- [ ] Client identity/role/scope headers are not trusted.
- [ ] Approved TLS hostname configured.
- [ ] Approved certificate installed without copying private key material into Git/package.
- [ ] Live TLS handshake validated.
- [ ] Monitoring target configured.
- [ ] Backup/restore test completed before Pilot activation.

## F. Activation gate

- [ ] Sanitized OP-04 evidence contains metadata/evidence references only.
- [ ] All nine OP-04 gates PASS.
- [ ] `DurableP2Bound=true` is based on physical validation, not config flags.
- [ ] `AuthoritativeP3Bound=true` is based on live domain mapping.
- [ ] Candidate gateway is real and is not `FailClosedCommandGateway`.
- [ ] Only after the above conditions may OP-05 End-to-End Pilot begin.

## G. Rollback

If validation fails before Oracle physical persistence is enabled:

- [ ] Stop EIMS IIS site/application.
- [ ] Preserve approved diagnostic logs/evidence.
- [ ] Remove/rename the EIMS application directory.
- [ ] Remove the Pilot IIS site/application if required by IT.
- [ ] Verify no operational database change occurred.

If physical EIMS Oracle persistence has been enabled:

- [ ] Stop application writes first.
- [ ] Follow DBA-approved restore/rollback procedure for the dedicated EIMS schema.
- [ ] Preserve audit/outbox evidence required for incident/change review.
- [ ] Do not delete or modify ERP/HR schemas as part of EIMS rollback.
