using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2c: evaluation completion commit (assignment completed + frozen assessment snapshot + plan readiness + optional G04 assessment
// + audit + outbox + idempotency) in ONE database transaction, plus G04 assessment / snapshot reads.
public sealed partial class OracleAuthorityStore
{
    private const string CompletionCommand = "evaluation-assignments.complete";

    private sealed class ConflictException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    public async ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(
        EvaluationCompletionRequest request, EvaluationCompletionCommit commit, CancellationToken cancellationToken = default)
    {
        var corr = request.Command.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, CompletionCommand, request.Command.IdeaId,
                request.Command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P2_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var idea = await ReadAggregateAsync(conn, tx, request.Idea.AggregateId, cancellationToken);
            if (idea is null)
                return AuthorityResult.Deny(404, "P2_AGGREGATE_NOT_FOUND", corr);

            // The plan row is locked so concurrent completions of one plan are serialized; readiness is then validated below.
            var plan = await ReadPlanAsync(conn, tx, request.Plan.PlanId, true, cancellationToken);
            var assignment = await ReadAssignmentAsync(conn, tx, request.Assignment.AssignmentId, cancellationToken);
            if (plan is null || assignment is null)
                return AuthorityResult.Deny(404, "P2_EVALUATION_CONTEXT_NOT_FOUND", corr);

            if (idea.Version != request.Idea.Version || idea.Version != request.Command.ExpectedIdeaVersion)
                return AuthorityResult.Deny(409, "P2_VERSION_CONFLICT", corr);
            if (plan.PlanVersion != request.Plan.PlanVersion || plan.PlanVersion != request.Command.ExpectedPlanVersion)
                return AuthorityResult.Deny(409, "P2_EVALUATION_PLAN_VERSION_CONFLICT", corr);
            if (assignment.AssignmentVersion != request.Assignment.AssignmentVersion
                || assignment.AssignmentVersion != request.Command.ExpectedAssignmentVersion)
                return AuthorityResult.Deny(409, "P2_EVALUATION_ASSIGNMENT_VERSION_CONFLICT", corr);

            if (!string.Equals(assignment.State, "PENDING", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(plan.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return AuthorityResult.Deny(409, "P2_EVALUATION_CONTEXT_NOT_MUTABLE", corr);

            var shapeError = TransactionalAuthorityStore.ValidateEvaluationCompletionShape(request, commit, idea, plan, assignment);
            if (shapeError is not null)
                return AuthorityResult.Deny(500, shapeError, corr);

            var events = commit.OutboxEvents.ToArray();
            if (events.Length is < 1 or > 2
                || events.GroupBy(x => x.MessageId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                return AuthorityResult.Deny(409, "P2_COMPLETION_OUTBOX_INVALID", corr);

            var required = await ReadRequiredAssignmentStatesAsync(conn, tx, plan.PlanId, cancellationToken);
            var expectedReadiness = required.All(x => string.Equals(
                string.Equals(x.Id, assignment.AssignmentId, StringComparison.Ordinal) ? commit.AssignmentAfter.State : x.State,
                "COMPLETED", StringComparison.OrdinalIgnoreCase));
            if (expectedReadiness != string.Equals(commit.PlanAfter.State, "READY_FOR_G04_DECISION", StringComparison.Ordinal))
                return AuthorityResult.Deny(500, "P2_EVALUATION_READINESS_MISMATCH", corr);
            if (expectedReadiness != (commit.G04Assessment is not null))
                return AuthorityResult.Deny(500, "P2_G04_ASSESSMENT_READINESS_MISMATCH", corr);

            var result = new AuthorityResult(
                200, "P2_EVALUATION_COMPLETION_ATOMIC_COMMIT", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.AssignmentAfter.AssignmentVersion, CorrelationId: corr,
                EmittedEvents: Array.AsReadOnly(events.Select(x => x.EventName).ToArray()));

            try
            {
                await UpdateAssignmentCompletionAsync(conn, tx, commit.AssignmentAfter, assignment.AssignmentVersion, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationAssignmentCompletionStaged);

                await InsertSnapshotAsync(conn, tx, commit.AssessmentSnapshot, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterAssessmentSnapshotStaged);

                await UpdatePlanReadinessAsync(conn, tx, commit.PlanAfter, plan.PlanVersion, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationPlanReadinessStaged);

                if (commit.G04Assessment is not null)
                    await InsertG04AssessmentAsync(conn, tx, commit.G04Assessment, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterG04AssessmentStaged);

                await InsertAuditAsync(conn, tx, idea, commit.Audit, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, idea, e, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterCompletionOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, CompletionCommand, request.Command.IdeaId,
                    request.Command.IdempotencyKey, request.IdempotencyFingerprint, result, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowIf(PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue; // concurrent same-key commit won the race: re-read it as replay/conflict
                if (Has(ex, "PK_EVALUATION_SNAPSHOT") || Has(ex, "UQ_EVAL_SNAPSHOT_ASSIGNMENT"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_ASSESSMENT_SNAPSHOT", corr);
                if (Has(ex, "PK_G04_ASSESSMENT") || Has(ex, "UQ_G04_ASSESS_PLAN"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_G04_ASSESSMENT", corr);
                if (Has(ex, "UQ_EIMS_AUDIT_ID"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG"))
                    return AuthorityResult.Deny(409, "P2_COMPLETION_OUTBOX_INVALID", corr);
                throw;
            }
            catch (ConflictException cx)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, cx.Code, corr);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    public async ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, G04Select + " WHERE PLAN_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadG04(r) : null;
    }

    /// <summary>Evidence read: the frozen assessment snapshot of one assignment (not part of IEvaluationWorkflowStore).</summary>
    public async Task<AssessmentSnapshotEnvelope?> GetAssessmentSnapshotForAssignmentAsync(
        string assignmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, SnapshotSelect + " WHERE ASSIGNMENT_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assignmentId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadSnapshot(r) : null;
    }

    // ---- transaction-scoped reads
    private static async Task<EvaluationPlanEnvelope?> ReadPlanAsync(
        OracleConnection conn, OracleTransaction tx, string planId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, PlanSelect + " WHERE ID = :p_id" + (forUpdate ? " FOR UPDATE" : ""));
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPlan(r) : null;
    }

    private static async Task<EvaluationAssignmentEnvelope?> ReadAssignmentAsync(
        OracleConnection conn, OracleTransaction tx, string assignmentId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, AssignmentSelect + " WHERE ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assignmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAssignment(r) : null;
    }

    private static async Task<List<(string Id, string State)>> ReadRequiredAssignmentStatesAsync(
        OracleConnection conn, OracleTransaction tx, string planId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "SELECT ID, STATE FROM EIMS_EVALUATION_ASSIGNMENT WHERE PLAN_ID = :p_id AND REQUIRED_FLAG = 1");
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(string, string)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    // ---- readers for S2c tables
    private static readonly string SnapshotSelect =
        "SELECT ID, ASSIGNMENT_ID, PLAN_ID, IDEA_ID, IDEA_VERSION, ROLE_CODE, SCOPE_CODE, SCHEMA_ID, SCHEMA_VERSION, OUTCOME, " +
        "NORMALIZED_JSON, CONTENT_SHA256, PERSON_ID, AUTHORITY_ASSIGNMENT_ID, " + Ts("CREATED_AT") + ", CORRELATION_ID FROM EIMS_EVALUATION_SNAPSHOT";

    private static readonly string G04Select =
        "SELECT ID, PLAN_ID, IDEA_ID, IDEA_VERSION, PLAN_VERSION, STATE, REQUIRED_SNAPSHOT_SHA256, " + Ts("CREATED_AT") +
        ", CORRELATION_ID, DECISION_ROUTE, DECISION_ROUTE_KIND, DECISION_METHOD, GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION, DECISION_VERSION " +
        "FROM EIMS_G04_ASSESSMENT";

    private static AssessmentSnapshotEnvelope ReadSnapshot(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Convert.ToInt64(r.GetValue(4)), r.GetString(5), r.GetString(6),
        r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13),
        ParseTs(r.GetString(14)), r.GetString(15));

    private static G04AssessmentEnvelope ReadG04(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToInt64(r.GetValue(3)), Convert.ToInt32(r.GetValue(4)), r.GetString(5),
        r.GetString(6), ParseTs(r.GetString(7)), r.GetString(8), Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12), Str(r, 13),
        Convert.ToInt32(r.GetValue(14)));

    // ---- writes
    private static async Task UpdateAssignmentCompletionAsync(
        OracleConnection conn, OracleTransaction tx, EvaluationAssignmentEnvelope a, int expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_EVALUATION_ASSIGNMENT SET STATE = :p_state, ENTITY_VERSION = :p_ver, COMPLETED_AT = :p_done, " +
            "COMPLETED_BY_PERSON_ID = :p_by, AUTHORITY_ASSIGNMENT_ID = :p_aid, ASSESSMENT_SCHEMA_ID = :p_sid, " +
            "ASSESSMENT_SCHEMA_VERSION = :p_sver, ASSESSMENT_OUTCOME = :p_out, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_state", OracleDbType.Varchar2, a.State);
        P(cmd, "p_ver", OracleDbType.Int32, a.AssignmentVersion);
        P(cmd, "p_done", OracleDbType.TimeStampTZ, a.CompletedAt);
        P(cmd, "p_by", OracleDbType.Varchar2, Nz(a.CompletedByPersonId));
        P(cmd, "p_aid", OracleDbType.Varchar2, Nz(a.AuthorityAssignmentId));
        P(cmd, "p_sid", OracleDbType.Varchar2, Nz(a.AssessmentSchemaId));
        P(cmd, "p_sver", OracleDbType.Varchar2, Nz(a.AssessmentSchemaVersion));
        P(cmd, "p_out", OracleDbType.Varchar2, Nz(a.AssessmentOutcome));
        P(cmd, "p_id", OracleDbType.Varchar2, a.AssignmentId);
        P(cmd, "p_expected", OracleDbType.Int32, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new ConflictException("P2_EVALUATION_ASSIGNMENT_VERSION_CONFLICT");
    }

    private static async Task UpdatePlanReadinessAsync(
        OracleConnection conn, OracleTransaction tx, EvaluationPlanEnvelope p, int expectedPlanVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_EVALUATION_PLAN SET PLAN_VERSION = :p_pv, STATE = :p_state, READY_AT = :p_ready, DECISION_ROUTE = :p_route, " +
            "DECISION_ROUTE_KIND = :p_kind, DECISION_METHOD = :p_method, GOVERNANCE_PROFILE_ID = :p_gid, " +
            "GOVERNANCE_PROFILE_VERSION = :p_gver, ENTITY_VERSION = ENTITY_VERSION + 1, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE ID = :p_id AND PLAN_VERSION = :p_expected");
        P(cmd, "p_pv", OracleDbType.Int32, p.PlanVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, p.State);
        P(cmd, "p_ready", OracleDbType.TimeStampTZ, p.ReadyAt);
        P(cmd, "p_route", OracleDbType.Varchar2, Nz(p.DecisionRoute));
        P(cmd, "p_kind", OracleDbType.Varchar2, Nz(p.DecisionRouteKind));
        P(cmd, "p_method", OracleDbType.Varchar2, Nz(p.DecisionMethod));
        P(cmd, "p_gid", OracleDbType.Varchar2, Nz(p.GovernanceProfileId));
        P(cmd, "p_gver", OracleDbType.Varchar2, Nz(p.GovernanceProfileVersion));
        P(cmd, "p_id", OracleDbType.Varchar2, p.PlanId);
        P(cmd, "p_expected", OracleDbType.Int32, expectedPlanVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new ConflictException("P2_EVALUATION_PLAN_VERSION_CONFLICT");
    }

    private static async Task InsertSnapshotAsync(OracleConnection conn, OracleTransaction tx, AssessmentSnapshotEnvelope s, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_EVALUATION_SNAPSHOT (ID, ASSIGNMENT_ID, PLAN_ID, IDEA_ID, IDEA_VERSION, ROLE_CODE, SCOPE_CODE, SCHEMA_ID, " +
            "SCHEMA_VERSION, OUTCOME, NORMALIZED_JSON, CONTENT_SHA256, PERSON_ID, AUTHORITY_ASSIGNMENT_ID, CORRELATION_ID, CREATED_AT) " +
            "VALUES (:p_id, :p_asg, :p_plan, :p_idea, :p_iv, :p_role, :p_scope, :p_sid, :p_sver, :p_out, :p_json, :p_sha, :p_person, :p_aid, :p_corr, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, s.SnapshotId);
        P(cmd, "p_asg", OracleDbType.Varchar2, s.EvaluationAssignmentId);
        P(cmd, "p_plan", OracleDbType.Varchar2, s.PlanId);
        P(cmd, "p_idea", OracleDbType.Varchar2, s.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, s.IdeaVersion);
        P(cmd, "p_role", OracleDbType.Varchar2, s.Role);
        P(cmd, "p_scope", OracleDbType.Varchar2, s.Scope);
        P(cmd, "p_sid", OracleDbType.Varchar2, s.SchemaId);
        P(cmd, "p_sver", OracleDbType.Varchar2, s.SchemaVersion);
        P(cmd, "p_out", OracleDbType.Varchar2, s.Outcome);
        P(cmd, "p_json", OracleDbType.Clob, s.NormalizedAssessmentJson);
        P(cmd, "p_sha", OracleDbType.Varchar2, s.ContentSha256);
        P(cmd, "p_person", OracleDbType.Varchar2, s.PersonId);
        P(cmd, "p_aid", OracleDbType.Varchar2, s.AuthorityAssignmentId);
        P(cmd, "p_corr", OracleDbType.Varchar2, s.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, s.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertG04AssessmentAsync(OracleConnection conn, OracleTransaction tx, G04AssessmentEnvelope g, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_ASSESSMENT (ID, PLAN_ID, IDEA_ID, IDEA_VERSION, PLAN_VERSION, STATE, REQUIRED_SNAPSHOT_SHA256, CORRELATION_ID, " +
            "DECISION_ROUTE, DECISION_ROUTE_KIND, DECISION_METHOD, GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION, DECISION_VERSION, CREATED_AT) " +
            "VALUES (:p_id, :p_plan, :p_idea, :p_iv, :p_pv, :p_state, :p_sha, :p_corr, :p_route, :p_kind, :p_method, :p_gid, :p_gver, :p_dv, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, g.AssessmentId);
        P(cmd, "p_plan", OracleDbType.Varchar2, g.PlanId);
        P(cmd, "p_idea", OracleDbType.Varchar2, g.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, g.IdeaVersion);
        P(cmd, "p_pv", OracleDbType.Int32, g.PlanVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, g.State);
        P(cmd, "p_sha", OracleDbType.Varchar2, g.RequiredAssignmentSnapshotSha256);
        P(cmd, "p_corr", OracleDbType.Varchar2, g.CorrelationId);
        P(cmd, "p_route", OracleDbType.Varchar2, Nz(g.DecisionRoute));
        P(cmd, "p_kind", OracleDbType.Varchar2, Nz(g.DecisionRouteKind));
        P(cmd, "p_method", OracleDbType.Varchar2, Nz(g.DecisionMethod));
        P(cmd, "p_gid", OracleDbType.Varchar2, Nz(g.GovernanceProfileId));
        P(cmd, "p_gver", OracleDbType.Varchar2, Nz(g.GovernanceProfileVersion));
        P(cmd, "p_dv", OracleDbType.Int32, g.DecisionVersion);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, g.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertIdempotencyRawAsync(
        OracleConnection conn, OracleTransaction tx, string command, string aggregateId, string key, string fingerprint,
        AuthorityResult result, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_IDEMPOTENCY (COMMAND_NAME, AGGREGATE_ID, IDEMPOTENCY_KEY, FINGERPRINT, RESULT_JSON) " +
            "VALUES (:p_cmd, :p_id, :p_key, :p_fp, :p_res)");
        P(cmd, "p_cmd", OracleDbType.Varchar2, command);
        P(cmd, "p_id", OracleDbType.Varchar2, aggregateId);
        P(cmd, "p_key", OracleDbType.Varchar2, key);
        P(cmd, "p_fp", OracleDbType.Varchar2, fingerprint);
        P(cmd, "p_res", OracleDbType.Clob, System.Text.Json.JsonSerializer.Serialize(result));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
