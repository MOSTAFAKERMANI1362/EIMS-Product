# EIMS P0 Machine Contract Recovery v1.0

## Status

`RECOVERED_REBASELINE_NOT_ORIGINAL`

This package is **not** the missing original P0 machine-readable bundle. It is a controlled recovery/rebaseline assembled only from authoritative evidence that remains available.

Frozen product source:

- `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`
- SHA-256 `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## Evidence used

- `EIMS_P0_Product_Contract_v1.0.md`
- `EIMS_P0_Gaps_TBD_v1.0.md`
- `EIMS_P0_API_Contract_Draft_v1.0.yaml`
- frozen v6.360 executable product specification
- `EIMS_P1_Completion_Review_v1.0.md`

The missing original P0 machine-readable artifacts are deliberately listed in the JSON recovery contract rather than silently recreated.

## Command recovery status

The P0 API draft identifies 21 mutating commands and their required roles. The historical P1 completion review reports a 28-command critical/reference catalog. The seven-command difference remains unidentified; this recovery does **not** invent their identities.

All 21 recovered API commands are therefore classified as `PARTIAL_EVIDENCE` and `executable=false` until a complete state + rule + event contract is proven for the individual command.

## Recovered fragments

The contract records only state/event fragments that are explicit in frozen v6.360 evidence, including selected G01/G02/G03/G04, Execution and Benefit transitions. A fragment is evidence for future recovery work; it is **not** permission to execute the Product command.

In particular:

- official G01→Case and G02→Need domain event names remain unresolved because P0 explicitly marked their Event mapping TBD;
- `IdeaApprovedForPortfolio.v1` and `BenefitHandoffRequested.v1` are retained because the frozen product contains those versioned event names;
- G04 committee voting remains separate from final `IDEA_DECISION` authority;
- 100% execution progress remains distinct from Completion;
- Benefit Claim / Verification / Attribution / Realization remain distinct;
- Knowledge validation and publication remain separate roles.

## Fail-closed rule

A recovered command may become executable only through a later controlled recovery/change set proving all required elements: role, scope, aggregate, complete state contract, complete rule contract, event contract, idempotency, optimistic version, SoD and atomic persistence.

Until then the P1 runtime must keep the command fail-closed.

## Verification

```powershell
dotnet run --project .\tools\EIMS.P0.MachineRecoveryVerifier\EIMS.P0.MachineRecoveryVerifier.csproj -c Release -- .\recovery\p0\P0_MACHINE_CONTRACT_RECOVERY_v1.0.json

dotnet run --project .\tests\EIMS.P0.MachineRecovery.ContractTests\EIMS.P0.MachineRecovery.ContractTests.csproj -c Release -- .\recovery\p0\P0_MACHINE_CONTRACT_RECOVERY_v1.0.json
```

The verifier protects provenance, command cardinality, fail-closed behavior, frozen SoD and the explicit recovered state/event fragments. The contract tests mutate critical fields to prove that accidental promotion, invented events/commands or authority drift are rejected.
