# EIMS P0 Machine Contract Recovery — Source Registry v1.0

**Recovery scope:** Wave 1 — G01 → G04  
**Status:** RECOVERY ARTIFACT / NOT ORIGINAL P0 PACKAGE  
**Frozen product:** `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`  
**Frozen SHA-256:** `057a224fa55e6ee17d128c41c86f3410206d7246723c35872f57757329c4e98a`

## 1. Purpose

The original standalone P0 machine-readable JSON/manifest files referenced by `EIMS_P0_Product_Contract_v1.0.md` are not physically recoverable from the currently accessible controlled sources. This Wave 1 package creates **new recovery artifacts** for the critical G01→G04 path. It never claims to recreate the missing originals byte-for-byte or semantically beyond the available evidence.

## 2. Authority hierarchy used for recovery

### PRIMARY — product authority
1. `EIMS_v6.360_WORKLIST_UX_ROLE_SELECTOR_CLEANUP.html`
2. `EIMS_P0_Product_Contract_v1.0.md`
3. `EIMS_P0_API_Contract_Draft_v1.0.yaml`
4. `EIMS_P0_Gaps_TBD_v1.0.md`

### COMPLETION EVIDENCE
- `EIMS_P1_Completion_Review_v1.0.md`

### SUPPORTING ENGINEERING EVIDENCE ONLY
- Approved/frozen EP engineering repositories. These may clarify terminology and existing engineering semantics but do not silently override the Product Contract or v6.360.

## 3. Recovery evidence classes

| Code | Meaning | May drive production mutation? |
|---|---|---:|
| `CONFIRMED_V6_360` | Directly evidenced by frozen executable product behavior | Yes, subject to server-authority composition |
| `CONFIRMED_P0_CONTRACT` | Explicit invariant/requirement in Product Contract | Yes |
| `PROPOSED_P0_API` | Explicit P0 proposed server command/role contract | Only after recovery/re-baseline acceptance |
| `SUPPORTING_ENGINEERING` | EP-level supporting semantics | No, not by itself |
| `TBD_UNRECOVERED` | Known gap or original identity not recovered | No — fail closed |

## 4. Explicitly missing original P0 artifacts

The following files are referenced by P0 but are not physically recoverable in the currently accessible sources:

- `EIMS_P0_Canonical_Model_v1.0.json`
- `EIMS_P0_State_Machines_v1.0.json`
- `EIMS_P0_Role_Permission_Matrix_v1.0.json`
- `EIMS_P0_Rule_Catalog_v1.0.json`
- `EIMS_P0_Event_Catalog_v1.0.json`
- `EIMS_P0_Traceability_Matrix_v1.0.json`
- `EIMS_P0_MANIFEST.txt`

They remain `SOURCE_MISSING`; this recovery does not replace their original identity.

## 5. Wave 1 evidence decisions

### G01
Frozen behavior proves:
- G01 decision role is `INTAKE_STEWARD` in the P0 API proposal.
- APPROVE converts the source to `CONVERTED_TO_CASE` and creates a Case in `UNDER_REVIEW`.
- RETURN → `G01_RETURNED`.
- HOLD → `HOLD`.
- REJECT → `G01_REJECTED`.
- The frozen code explicitly records the Case creation semantic with **Event mapping TBD**.

Therefore the original stabilized P1 event identity for G01→Case remains unrecovered and must not be invented.

### G02
Frozen behavior proves:
- G02 decision role is `CASE_REVIEWER` in the P0 API proposal.
- `NEED_CANDIDATE` → Case `APPROVED` and Need `DRAFT` creation.
- `ROUTE_OPERATIONAL` → `ROUTED_OPERATIONAL`.
- `RETURN` → `RETURNED`.
- `HOLD` → `HOLD`.
- `REJECT` → `REJECTED`.
- G02 decision history is snapshot-based/immutable in v6.360.

The original stabilized P1 event identity for G02→Need remains unrecovered.

### G03
Frozen behavior proves:
- Need Owner submits an eligible Need for independent G03 review.
- `DRAFT` → `PENDING_G03_REVIEW` with `g03ReviewStatus=PENDING`.
- Need Reviewer APPROVE → `READY_FOR_IDEATION`, `g03ReviewStatus=APPROVED`, routed to `IDEA_OWNER`.
- Need Reviewer RETURN → `DRAFT`, `g03ReviewStatus=RETURNED`, routed back to `NEED_OWNER`.
- `READY_FOR_IDEATION` without G03 approval evidence is treated as inconsistent and cannot open ideation.
- Observed audit identities include `NeedSubmittedForG03Review`, `NeedApprovedAtG03AndRoutedToIdeationOwner`, and `NeedReturnedByG03Reviewer`.

### G04
Frozen behavior proves:
- Idea submission changes Idea to `UNDER_REVIEW`, creates a pending G04 assessment, and freezes strategy/version context.
- observed audit: `IdeaSubmittedForAssessment` and `AssessmentCreated`.
- APPROVE → Idea `APPROVED` and explicit domain event `IdeaApprovedForPortfolio.v1`.
- RETURN → `RETURNED`.
- HOLD → `HOLD`.
- REJECT → `REJECTED`.
- P0 API separates committee vote (`G04_COMMITTEE_MEMBER`) from final decision (`IDEA_DECISION`).
- Dynamic Evaluation Plan remains the source of truth for specialist evaluation requirements.

## 6. Recovery restrictions

- No original P0 filename is reused as though recovered.
- `TBD_UNRECOVERED` semantics cannot be promoted to authoritative/ready by the recovery verifier.
- EP engineering event names are not substituted for the two unresolved P1 event identities.
- Thresholds, weights, scoring and hard conditions are not redesigned in this recovery.
- `v6.360` is never changed by this operation.

## 7. Next waves

- Wave 2: Portfolio → Execution → Benefit → Knowledge machine contract recovery.
- Cross-cutting: full role/scope recovery, event/rule catalog reconciliation, and resolution of the 21/28 P1 command-catalog discrepancy.
