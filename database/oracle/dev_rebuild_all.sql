-- DEV ONLY: drops everything, rebuilds V001-V004, runs every verify. Run as EIMS_OWNER from this folder:
--   sqlplus EIMS_OWNER@localhost:1521/FREEPDB1   then   @dev_rebuild_all.sql
-- Output is also saved to dev_rebuild_output.txt (send that file back; it contains no passwords).
SET SERVEROUTPUT ON SIZE UNLIMITED
SET FEEDBACK OFF
SET LINESIZE 200
SPOOL dev_rebuild_output.txt
PROMPT === RESET (ORA-00942 errors are harmless on first run) ===
@@99_dev_reset.sql
PROMPT === MIGRATIONS ===
@@migrations/V001__core_persistence.sql
@@migrations/V002__evaluation_core.sql
@@migrations/V003__g04_assessment.sql
@@migrations/V004__portfolio_execution_benefit_knowledge.sql
PROMPT === VERIFY ===
@@verify/verify_core.sql
@@verify/verify_evaluation_core.sql
@@verify/verify_g04_assessment.sql
@@verify/verify_waves_9_13.sql
@@verify/verify_conventions.sql
PROMPT === DONE ===
SPOOL OFF
