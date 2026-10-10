SET SERVEROUTPUT ON
SET VERIFY OFF
SET FEEDBACK OFF

DECLARE
  v_cnt NUMBER;
  v_rows NUMBER;
  v_id VARCHAR2(64) := 'T-G04-A1';
  v_plan VARCHAR2(64) := 'T-PLAN-G04-1';
  v_snap VARCHAR2(64) := 'T-G04-SNAP-1';
  v_vote VARCHAR2(64) := 'T-G04-VOTE-1';
BEGIN
  SELECT COUNT(*) INTO v_cnt
  FROM user_tables
  WHERE table_name IN (
    'EIMS_G04_ASSESSMENT',
    'EIMS_G04_GOVERNANCE_SNAPSHOT',
    'EIMS_G04_COMMITTEE_VOTE'
  );

  IF v_cnt = 3 THEN
    DBMS_OUTPUT.PUT_LINE('PASS 3 G04 tables exist');
  ELSE
    DBMS_OUTPUT.PUT_LINE('FAIL expected 3 G04 tables, found ' || v_cnt);
  END IF;

  INSERT INTO EIMS_EVALUATION_PLAN
    (ID, IDEA_ID, IDEA_VERSION, PLAN_VERSION, STATE, CORRELATION_ID,
     DECISION_ROUTE, CREATED_AT)
  VALUES
    (v_plan, 'T-IDEA-G04', 1, 1, 'READY_FOR_G04_DECISION', 'C-G04',
     'G04_COMMITTEE', SYSTIMESTAMP);

  INSERT INTO EIMS_G04_ASSESSMENT
    (ID, IDEA_ID, IDEA_VERSION, EVALUATION_PLAN_ID, STATUS, PRIORITY,
     RULE_SET, ASSIGNED_ROLE, DECISION_ROUTE, DECISION_METHOD,
     G04_PROFILE_ID, CALCULATED_GATE_SCORE, ENTITY_VERSION)
  VALUES
    (v_id, 'T-IDEA-G04', 1, v_plan, 'PENDING', 'NORMAL',
     'G04-RS-1.3', 'IDEA_DECISION', 'G04_COMMITTEE',
     'MAJORITY', 'G04-GENERAL-V1.0', 78, 1);

  INSERT INTO EIMS_G04_GOVERNANCE_SNAPSHOT
    (ID, ASSESSMENT_ID, PROFILE_ID, DECISION_ROUTE, VOTE_RULE,
     QUORUM_REQUIRED, CHAIR_PERSON_ID, MEMBER_SNAPSHOT, PROFILE_SNAPSHOT)
  VALUES
    (v_snap, v_id, 'G04-GENERAL-V1.0', 'G04_COMMITTEE', 'MAJORITY', 2,
     'P-CHAIR', '[{"member":"M1"},{"member":"M2"}]',
     '{"passThreshold":65,"dataThreshold":80}');

  BEGIN
    UPDATE EIMS_G04_GOVERNANCE_SNAPSHOT
       SET PROFILE_ID = 'MUTATION'
     WHERE ID = v_snap;
    DBMS_OUTPUT.PUT_LINE('FAIL governance snapshot UPDATE was allowed');
  EXCEPTION
    WHEN OTHERS THEN
      IF SQLCODE = -20041 THEN
        DBMS_OUTPUT.PUT_LINE('PASS governance snapshot UPDATE is blocked');
      ELSE
        DBMS_OUTPUT.PUT_LINE('FAIL governance snapshot UPDATE raised ' || SQLERRM);
      END IF;
  END;

  INSERT INTO EIMS_G04_COMMITTEE_VOTE
    (ID, ASSESSMENT_ID, MEMBER_KEY, VOTE, NOTE)
  VALUES
    (v_vote, v_id, 'M1', 'APPROVE', 'Initial committee vote for test');

  BEGIN
    INSERT INTO EIMS_G04_COMMITTEE_VOTE
      (ID, ASSESSMENT_ID, MEMBER_KEY, VOTE, NOTE)
    VALUES
      ('T-G04-VOTE-2', v_id, 'M1', 'REJECT', 'Duplicate member test');
    DBMS_OUTPUT.PUT_LINE('FAIL duplicate committee member vote was accepted');
  EXCEPTION
    WHEN DUP_VAL_ON_INDEX THEN
      DBMS_OUTPUT.PUT_LINE('PASS duplicate committee member vote rejected');
  END;

  INSERT INTO EIMS_G04_COMMITTEE_VOTE
    (ID, ASSESSMENT_ID, MEMBER_KEY, VOTE, NOTE)
  VALUES
    ('T-G04-VOTE-2', v_id, 'M2', 'APPROVE', 'Second committee vote');

  BEGIN
    UPDATE EIMS_G04_ASSESSMENT
       SET ENTITY_VERSION = ENTITY_VERSION + 1
     WHERE ID = v_id AND ENTITY_VERSION = 1;
    v_rows := SQL%ROWCOUNT;

    IF v_rows = 1 THEN
      DBMS_OUTPUT.PUT_LINE('PASS optimistic G04 assessment update with correct version changes 1 row');
    ELSE
      DBMS_OUTPUT.PUT_LINE('FAIL optimistic G04 assessment update changed ' || v_rows || ' rows');
    END IF;
  END;

  UPDATE EIMS_G04_ASSESSMENT
     SET ENTITY_VERSION = ENTITY_VERSION + 1
   WHERE ID = v_id AND ENTITY_VERSION = 1;

  v_rows := SQL%ROWCOUNT;

  IF v_rows = 0 THEN
    DBMS_OUTPUT.PUT_LINE('PASS stale G04 assessment version changes 0 rows (conflict detected)');
  ELSE
    DBMS_OUTPUT.PUT_LINE('FAIL stale G04 assessment version changed ' || v_rows || ' rows');
  END IF;

  BEGIN
    INSERT INTO EIMS_G04_GOVERNANCE_SNAPSHOT
      (ID, ASSESSMENT_ID, PROFILE_ID, DECISION_ROUTE, VOTE_RULE,
       QUORUM_REQUIRED, MEMBER_SNAPSHOT, PROFILE_SNAPSHOT)
    VALUES
      ('T-G04-SNAP-2', v_id, 'G04-GENERAL-V1.0', 'G04_COMMITTEE',
       'MAJORITY', 2, '[]', '{"passThreshold":65}');
    DBMS_OUTPUT.PUT_LINE('FAIL duplicate governance snapshot accepted');
  EXCEPTION
    WHEN DUP_VAL_ON_INDEX THEN
      DBMS_OUTPUT.PUT_LINE('PASS duplicate governance snapshot per assessment rejected');
  END;

  BEGIN
    INSERT INTO EIMS_G04_ASSESSMENT
      (ID, IDEA_ID, IDEA_VERSION, EVALUATION_PLAN_ID, STATUS, PRIORITY,
       RULE_SET, ASSIGNED_ROLE, DECISION_ROUTE, DECISION_METHOD, ENTITY_VERSION)
    VALUES
      ('T-G04-A2', 'T-IDEA-G04', 1, v_plan, 'PENDING', 'NORMAL',
       'G04-RS-1.3', 'IDEA_DECISION', 'UNIT_RND_DECISION', 'INDIVIDUAL', 1);
    DBMS_OUTPUT.PUT_LINE('FAIL duplicate G04 assessment for plan accepted');
  EXCEPTION
    WHEN DUP_VAL_ON_INDEX THEN
      DBMS_OUTPUT.PUT_LINE('PASS duplicate G04 assessment for evaluation plan rejected');
  END;

  ROLLBACK;
  DBMS_OUTPUT.PUT_LINE('Test rows rolled back.');
END;
/
