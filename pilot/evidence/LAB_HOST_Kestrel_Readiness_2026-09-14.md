# LAB Host Kestrel Readiness Evidence — 2026-09-14

Classification: `LAB_EVIDENCE`

Sanitized results from the temporary Windows 11 lab host:

- P5 ASP.NET Core host started successfully under Kestrel on loopback `http://127.0.0.1:5090`.
- `/health` returned `status = Healthy`.
- Reported assembly: `P5-1.0.0`.
- Frozen product baseline identity returned correctly: `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html` with the expected baseline SHA-256.
- `serverAuthorityBoundary = true`.
- `domainCommandAuthorityBound = false`, as expected before P1 runtime composition.
- `/api/pilot/readiness` returned `ready = false`, which is the expected fail-closed behavior at this stage.

Gate interpretation (`PilotGateStatus`: `0=Ready`, `1=Blocked`, `2=TestRequired`):

- `baseline.freeze` = Ready.
- `p3.windows.config` = Ready at configuration-contract level (Windows hosting contract configured to require authenticated Windows user and distrust client identity headers).
- `p1.authority.runtime` = Blocked — physical P1 authority runtime not yet composed.
- `p1.authority.contract-tests` = Blocked because prerequisite P1 binding is incomplete.
- `p2.oracle.binding` = Blocked — Oracle version/provider/connectivity/service-account/schema-owner not yet bound.
- `p2.oracle.live` = Blocked because Oracle binding prerequisite is incomplete.
- `p3.windows.live` = TestRequired — live Domain account → principal → PersonID → Role/Scope evidence not yet recorded.
- `infra.windows.vm` = Blocked for final Network Pilot because the current PC is only a temporary lab host, not the final organizational Windows Server/VM.

Conclusion:

The P5 host is locally executable and healthy, and its readiness evaluator correctly refuses to declare Network Pilot readiness without the remaining authority, Oracle, live identity, TLS, P4 real export, concurrency/idempotency, audit/outbox, backup/restore and monitoring evidence.

No host/user identifiers, credentials, connection strings or production data are stored in this evidence record.
