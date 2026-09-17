# EIMS Wave16 — Production Runtime Composition Binding Plan

## Current merged behavior

Wave15 introduced `IProductionRuntimeComposition` and a safe default `UnavailableProductionRuntimeComposition`. `AddEimsRuntimeActivation(...)` uses `TryAddSingleton`, so an environment-specific composition may register before the activation extension. If no real composition is supplied, the Host remains fail-closed.

## Required real composition responsibilities

The environment-specific implementation must expose only validated server-side facts:

- `DurableP2Bound = true` only after the real persistence layer is bound to the approved Oracle environment and physical transaction behavior is validated.
- `AuthoritativeP3Bound = true` only after Windows principal -> PersonID -> exact Assignment -> Role/Scope resolution is validated against the real domain/directory source.
- `CandidateGateway` must be the real P1-P5 gateway composed with the durable P2 and authoritative P3 dependencies.
- `CompositionEvidenceRef` may hold a non-sensitive internal evidence identifier. It must not contain credentials, connection strings, certificate material or personal data.

## Registration order

The real environment adapter must register `IProductionRuntimeComposition` before:

`builder.Services.AddEimsRuntimeActivation(builder.Configuration, builder.Environment.ContentRootPath);`

The Wave15 `TryAddSingleton` default then remains unused. Configuration flags are diagnostic only and cannot replace this registration.

## Oracle/P2 rule

`PersistenceContractDescriptor.RecoveryBaseline()` is logically ready but its `OracleBindingEvidence` is unbound. The production adapter must not set durable P2 true merely because Oracle fields are configured. A live connection plus physical transaction/concurrency/idempotency evidence is required.

## P3 rule

Do not accept client-supplied identity, role or scope. The production composition must use the real authenticated Windows principal and authoritative server-side mapping. HR job title is not EIMS role authority.

## Gateway rule

The candidate gateway must not be `FailClosedCommandGateway` and must preserve the merged P1-P5 v1.3.0 command surface (32 recovered user mutations). System event intake remains outside the user HTTP command surface.

## Activation sequence

1. Build physical P2 adapter against approved Oracle provider/environment.
2. Validate Oracle live connection and transaction behavior.
3. Bind real P3 directory/assignment resolution to Windows Authentication.
4. Compose the real P1-P5 gateway using those dependencies.
5. Register the real `IProductionRuntimeComposition` before `AddEimsRuntimeActivation`.
6. Load sanitized OP-04 `PILOT_ENVIRONMENT_EVIDENCE` through `Pilot:Activation:Op04EvidencePath`.
7. Confirm all nine OP-04 gates PASS.
8. Confirm Wave15 second-layer composition gate reports no blockers.
9. Only then proceed to OP-05 End-to-End Pilot.

## Current blockers

At Wave16 start, the repository has no validated physical Oracle binding and no validated live Domain/IIS/TLS/HR environment evidence. Therefore a real production composition must not yet report ready.
