-- EIMS S3 verification of V005 (outbox claim columns, indexes, claim/finalize semantics). Run as EIMS_OWNER. Rows are rolled back.
SET SERVEROUTPUT ON
SET VERIFY OFF
DECLARE
  v_cnt NUMBER;
  v_rows NUMBER;
  PROCEDURE rep(p_name VARCHAR2, p_ok BOOLEAN) IS
  BEGIN DBMS_OUTPUT.PUT_LINE(CASE WHEN p_ok THEN 'PASS ' ELSE 'FAIL ' END || p_name); END;
BEGIN
  SELECT COUNT(*) INTO v_cnt FROM user_tab_columns WHERE table_name = 'EIMS_OUTBOX' AND column_name IN ('CLAIM_TOKEN','CLAIMED_UNTIL');
  rep('outbox has CLAIM_TOKEN and CLAIMED_UNTIL', v_cnt = 2);
  SELECT COUNT(*) INTO v_cnt FROM user_indexes WHERE index_name IN ('IX_EIMS_OUTBOX_READY','IX_EIMS_OUTBOX_AGG_SEQ','IX_EIMS_OUTBOX_TOKEN');
  rep('3 dispatcher indexes exist', v_cnt = 3);

  INSERT INTO EIMS_OUTBOX (MESSAGE_ID, EVENT_NAME, AGGREGATE_ID, AGGREGATE_TYPE, AGGREGATE_VERSION, CORRELATION_ID, OCCURRED_AT, PAYLOAD_JSON)
  VALUES ('T-OB-1', 'E', 'T-AGG', 'T-TYPE', 1, 'C', SYSTIMESTAMP, '{}');
  UPDATE EIMS_OUTBOX SET CLAIM_TOKEN = 'W1:tok', CLAIMED_UNTIL = SYSTIMESTAMP + INTERVAL '30' SECOND WHERE MESSAGE_ID = 'T-OB-1' AND CLAIM_TOKEN IS NULL;
  v_rows := SQL%ROWCOUNT;
  rep('claim with a token changes 1 row', v_rows = 1);
  UPDATE EIMS_OUTBOX SET DISPATCH_STATUS = 'SENT' WHERE MESSAGE_ID = 'T-OB-1' AND CLAIM_TOKEN = 'W2:stale';
  v_rows := SQL%ROWCOUNT;
  rep('finalizing with a stale token changes 0 rows', v_rows = 0);
  BEGIN
    UPDATE EIMS_OUTBOX SET DISPATCH_STATUS = 'DEAD' WHERE MESSAGE_ID = 'T-OB-1';
    rep('invalid dispatch status rejected', FALSE);
  EXCEPTION WHEN OTHERS THEN rep('invalid dispatch status rejected', SQLCODE = -2290); END;
  ROLLBACK;
  DBMS_OUTPUT.PUT_LINE('Test rows rolled back.');
END;
/
