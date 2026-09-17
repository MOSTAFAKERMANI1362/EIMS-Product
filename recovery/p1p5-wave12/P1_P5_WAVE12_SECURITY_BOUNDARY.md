# Wave12 Security Boundary

Status: DIFFERENTIAL REVIEW PASS — 2026-09-17

- Windows principal is resolved through P3; request body identity/role/scope fields are not trusted authority.
- Exact assignment context is required.
- `requestedScope` may only narrow an already-authorized P3 scope; it cannot create authority.
- Execution ownership is re-resolved server-side and the P5 binding targets `ExecutionServiceWave10Guarded` only.
- Benefit ownership and specialist evidence are resolved server-side by `BenefitServiceWave11`.
- Completion reviewer and Benefit verifier separation-of-duties rules remain domain-enforced.
- System event intake handlers are not registered as user commands.
- Missing Execution/Benefit executors fail closed.
- Legacy grouped Execution mutation remains unavailable.
- Pilot Host remains fail closed pending physical P2/P3/OP-04 readiness.
- No new external PackageReference/dependency, secret, credential, connection string, customer data or HR data was introduced by Wave12.
- GitHub Actions remain pinned to immutable SHAs; hosted runs that terminated with `steps=null` are treated as infrastructure-only and not as application test results.
