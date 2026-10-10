-- EIMS S1b-1 verification
-- Run as EIMS_OWNER inside FREEPDB1.
-- Every PASS is a contract assertion; all test rows are rolled back.

SET SERVEROUTPUT ON
SET VERIFY OFF

DECLARE
    v_count NUMBER;
    v_rows  NUMBER;
    v_plan  VARCHAR2(100) := 'TEST-EVPLAN-001';
    v_asg   VARCHAR2(100) := 'TEST-EVASG-001';
    v_snap  VARCHAR2(100) := 'TEST-EVSNAP-001';
    v_plan2 VARCHAR2(100) := 'TEST-EVPLAN-002';
    v_snap2 VARCHAR2(100) := 'TEST-EVSNAP-002';
BEGIN
    SELECT COUNT(*) INTO v_count
      FROM user_tables
     WHERE table_name IN (
        'EIMS_EVALUATION_PLAN',
        'EIMS_EVALUATION_ASSIGNMENT',
        'EIMS_EVALUATION_SNAPSHOT'
     );

    IF v_count = 3 THEN
        DBMS_OUTPUT.PUT_LINE('PASS 3 evaluation-core tables exist');
    ELSE
        RAISE_APPLICATION_ERROR(-20101, 'FAIL evaluation-core table count=' || v_count);
    END IF;

    INSERT INTO EIMS_EVALUATION_PLAN
        (ID, IDEA_ID, IDEA_VERSION, STATUS, ENTITY_VERSION, DECISION_ROUTE, RULE_SET, PASSPORT_SNAPSHOT)
    VALUES
        (v_plan, 'TEST-IDEA-001', 3, 'ACTIVE', 1, 'UNIT_RND_DECISION',
         'G04-DYNAMIC-ROUTING-1.0', '{"scope":"UNIT","costType":"OPEX"}');

    INSERT INTO EIMS_EVALUATION_ASSIGNMENT
        (ID, PLAN_ID, ROLE_CODE, ROLE_LABEL, REQUIRED_FLAG, STATUS, ENTITY_VERSION)
    VALUES
        (v_asg, v_plan, 'IDEA_EVALUATOR', 'ارزیاب ساختاریافته ایده', 1, 'PENDING', 1);

    INSERT INTO EIMS_EVALUATION_SNAPSHOT
        (ID, PLAN_ID, IDEA_ID, IDEA_VERSION, SNAPSHOT_TYPE, PAYLOAD)
    VALUES
        (v_snap, v_plan, 'TEST-IDEA-001', 3, 'PROFILE',
         '{"ideaVersion":3,"scope":"UNIT","costType":"OPEX"}');

    UPDATE EIMS_EVALUATION_ASSIGNMENT
       SET STATUS='COMPLETED', ENTITY_VERSION=2
     WHERE ID=v_asg AND ENTITY_VERSION=1;

    v_rows := SQL%ROWCOUNT;
    IF v_rows = 1 THEN
        DBMS_OUTPUT.PUT_LINE('PASS optimistic assignment update with correct version changes 1 row');
    ELSE
        RAISE_APPLICATION_ERROR(-20102, 'FAIL optimistic assignment update');
    END IF;

    UPDATE EIMS_EVALUATION_ASSIGNMENT
       SET STATUS='IN_PROGRESS', ENTITY_VERSION=3
     WHERE ID=v_asg AND ENTITY_VERSION=1;

    v_rows := SQL%ROWCOUNT;
    IF v_rows = 0 THEN
        DBMS_OUTPUT.PUT_LINE('PASS stale assignment version changes 0 rows (conflict detected)');
    ELSE
        RAISE_APPLICATION_ERROR(-20103, 'FAIL stale assignment version');
    END IF;

    BEGIN
        INSERT INTO EIMS_EVALUATION_PLAN
            (ID, IDEA_ID, IDEA_VERSION, STATUS, ENTITY_VERSION, PASSPORT_SNAPSHOT)
        VALUES
            (v_plan2, 'TEST-IDEA-001', 3, 'ACTIVE', 1, '{}');

        RAISE_APPLICATION_ERROR(-20104, 'FAIL duplicate idea/version was accepted');
    EXCEPTION
        WHEN DUP_VAL_ON_INDEX THEN
            DBMS_OUTPUT.PUT_LINE('PASS duplicate active-plan identity rejected');
    END;

    BEGIN
        UPDATE EIMS_EVALUATION_SNAPSHOT
           SET PAYLOAD='{"changed":true}'
         WHERE ID=v_snap;

        RAISE_APPLICATION_ERROR(-20105, 'FAIL snapshot UPDATE was accepted');
    EXCEPTION
        WHEN OTHERS THEN
            IF SQLCODE = -20031 THEN
                DBMS_OUTPUT.PUT_LINE('PASS snapshot UPDATE is blocked');
            ELSE
                RAISE;
            END IF;
    END;

    BEGIN
        DELETE FROM EIMS_EVALUATION_SNAPSHOT WHERE ID=v_snap;

        RAISE_APPLICATION_ERROR(-20106, 'FAIL snapshot DELETE was accepted');
    EXCEPTION
        WHEN OTHERS THEN
            IF SQLCODE = -20031 THEN
                DBMS_OUTPUT.PUT_LINE('PASS snapshot DELETE is blocked');
            ELSE
                RAISE;
            END IF;
    END;

    BEGIN
        INSERT INTO EIMS_EVALUATION_SNAPSHOT
            (ID, PLAN_ID, IDEA_ID, IDEA_VERSION, SNAPSHOT_TYPE, PAYLOAD)
        VALUES
            (v_snap2, v_plan, 'TEST-IDEA-001', 3, 'PROFILE',
             '{"duplicateType":true}');

        RAISE_APPLICATION_ERROR(-20107, 'FAIL duplicate snapshot type was accepted');
    EXCEPTION
        WHEN DUP_VAL_ON_INDEX THEN
            DBMS_OUTPUT.PUT_LINE('PASS duplicate snapshot type per plan rejected');
    END;

    BEGIN
        INSERT INTO EIMS_EVALUATION_PLAN
            (ID, IDEA_ID, IDEA_VERSION, STATUS, ENTITY_VERSION, PASSPORT_SNAPSHOT)
        VALUES
            ('TEST-EVPLAN-BADJSON', 'TEST-IDEA-JSON', 1, 'ACTIVE', 1, 'not-json');

        RAISE_APPLICATION_ERROR(-20108, 'FAIL invalid plan JSON was accepted');
    EXCEPTION
        WHEN OTHERS THEN
            IF SQLCODE IN (-2290, -20008) OR SQLERRM LIKE '%JSON%' THEN
                DBMS_OUTPUT.PUT_LINE('PASS invalid plan JSON rejected');
            ELSE
                RAISE;
            END IF;
    END;

    ROLLBACK;
    DBMS_OUTPUT.PUT_LINE('Test rows rolled back.');
END;
/
