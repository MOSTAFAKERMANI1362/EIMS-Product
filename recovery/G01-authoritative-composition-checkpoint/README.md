# A01-EIMS — G01 Recovery Checkpoint

Status: RECOVERY PRESERVED — NOT FROZEN / NOT PRODUCTION READY

## Purpose
This branch preserves the complete G01 authoritative-composition attempt so work can continue outside GitHub when an execution environment is available.

## Stable restore point
Stable project checkpoint:
- Branch: feature/vs01-observation-g01
- Commit: 1375372d6cc1a31eee6d2d44676ee9f2f651a0f7
- Meaning: last validated G01 checkpoint before authoritative-composition implementation.

## Preserved experimental state
- Recovery branch: recovery/G01-authoritative-composition-checkpoint
- Current preserved head: 4223e1c8ac8c21815f7830e75a249bc543a68a90
- This branch contains the complete six-commit attempt after the stable checkpoint.

## Delta from stable checkpoint
Only these files changed:
1. src/EIMS.Authority.Recovery/AuthorityKernel.cs
2. src/EIMS.Authority.Recovery/G01RuleExecutionComposer.cs
3. tests/EIMS.Persistence.Recovery.ContractTests/Program.cs

No Oracle SQL/DDL was introduced.

## Evidence
Before the final correction, the authoritative-composition implementation reached CI with the new composition/decision-path tests passing; one R03 missing-review behavior exposed a classification defect. That defect was corrected in 4223e1c8.
The subsequent CI attempt did not provide executable test evidence and must not be treated as PASS.

## Recovery rules
- Do not merge this branch.
- Do not treat its implementation as production-ready.
- Compare/review the three changed files against 1375372d before continuing.
- Reuse existing G01 contracts; do not reopen frozen decisions without conflict/evidence/change request.
- Local validation must be performed before any new CI.
- The stable feature branch is intentionally reset to 1375372d.

## External continuation
If GitHub tooling is unavailable, obtain the repository at the stable checkpoint and this recovery branch from GitHub when possible. The three-file delta is sufficient to reconstruct the attempted implementation.
