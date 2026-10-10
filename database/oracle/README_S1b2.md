# EIMS S1b-2 — G04 Assessment

This migration is based on the G04 behavior found in the v6.360 prototype.

Confirmed prototype behavior:
- A G04 assessment is created only after the active Evaluation Plan is ready/completed.
- The assessment is linked to the Evaluation Plan and freezes the Idea version.
- The assessment stores the decision route, rule set, assigned role, G04 profile reference, strategy references and calculated gate score.
- A frozen G04 governance/profile snapshot is retained with the assessment.
- Committee route has a governance snapshot including vote rule, quorum and member snapshot.
- Committee votes are separate records and are unique per assessment/member.
- Final G04 decisions are APPROVE / RETURN / HOLD / REJECT.
- Decision comments are required and auditable in the prototype.

This migration intentionally does not create downstream Portfolio/Execution/Benefit/Knowledge tables.
Those belong to S1b-3.

Run as EIMS_OWNER inside FREEPDB1:
  @migrations/V003__g04_assessment.sql
  @verify/verify_g04_assessment.sql
