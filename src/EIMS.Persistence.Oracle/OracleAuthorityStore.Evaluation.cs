using System.Data.Common;
using System.Globalization;
using EIMS.Authority.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2b: evaluation plan + assignment persistence (tables EIMS_EVALUATION_PLAN / EIMS_EVALUATION_ASSIGNMENT).
// Evaluation COMPLETION and G04 assessment are S2c and fail closed.
public sealed partial class OracleAuthorityStore
{
    public async ValueTask<EvaluationPlanEnvelope?> GetEvaluationPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, PlanSelect + " WHERE ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadPlan(r) : null;
    }

    public async ValueTask<EvaluationAssignmentEnvelope?> GetEvaluationAssignmentAsync(
        string evaluationAssignmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, AssignmentSelect + " WHERE ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, evaluationAssignmentId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadAssignment(r) : null;
    }

    public async ValueTask<IReadOnlyCollection<EvaluationAssignmentEnvelope>> GetEvaluationAssignmentsForPlanAsync(
        string planId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, AssignmentSelect + " WHERE PLAN_ID = :p_id ORDER BY ID");
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<EvaluationAssignmentEnvelope>();
        while (await r.ReadAsync(cancellationToken)) list.Add(ReadAssignment(r));
        return list;
    }

    // ---- S2c boundary: fail closed, never silently empty
    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("G04 assessment persistence is bound in step S2c.");

    public ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(
        EvaluationCompletionRequest request, EvaluationCompletionCommit commit, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AuthorityResult.Deny(501, "P2_ORACLE_EVALUATION_COMPLETION_NOT_BOUND", request.Command.CorrelationId,
            "Evaluation completion persistence is bound in step S2c."));

    // ---- helpers
    private static string Ts(string column) =>
        "TO_CHAR(SYS_EXTRACT_UTC(" + column + "), 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6')";

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static readonly string PlanSelect =
        "SELECT ID, IDEA_ID, IDEA_VERSION, PLAN_VERSION, STATE, " + Ts("CREATED_AT") + ", CORRELATION_ID, " + Ts("READY_AT") +
        ", DECISION_ROUTE, DECISION_ROUTE_KIND, DECISION_METHOD, GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION FROM EIMS_EVALUATION_PLAN";

    private static readonly string AssignmentSelect =
        "SELECT ID, PLAN_ID, IDEA_ID, IDEA_VERSION, ROLE_CODE, SCOPE_CODE, REQUIRED_FLAG, STATE, " + Ts("CREATED_AT") +
        ", CORRELATION_ID, ENTITY_VERSION, " + Ts("COMPLETED_AT") +
        ", COMPLETED_BY_PERSON_ID, AUTHORITY_ASSIGNMENT_ID, ASSESSMENT_SCHEMA_ID, ASSESSMENT_SCHEMA_VERSION, ASSESSMENT_OUTCOME FROM EIMS_EVALUATION_ASSIGNMENT";

    private static EvaluationPlanEnvelope ReadPlan(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), Convert.ToInt64(r.GetValue(2)), Convert.ToInt32(r.GetValue(3)), r.GetString(4),
        ParseTs(r.GetString(5)), r.GetString(6), r.IsDBNull(7) ? null : ParseTs(r.GetString(7)),
        Str(r, 8), Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12));

    private static EvaluationAssignmentEnvelope ReadAssignment(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToInt64(r.GetValue(3)), r.GetString(4), r.GetString(5),
        Convert.ToInt32(r.GetValue(6)) == 1, r.GetString(7), ParseTs(r.GetString(8)), r.GetString(9),
        Convert.ToInt32(r.GetValue(10)), r.IsDBNull(11) ? null : ParseTs(r.GetString(11)),
        Str(r, 12), Str(r, 13), Str(r, 14), Str(r, 15), Str(r, 16));

    private static async Task InsertPlanAsync(OracleConnection conn, OracleTransaction tx, EvaluationPlanEnvelope p, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_EVALUATION_PLAN (ID, IDEA_ID, IDEA_VERSION, PLAN_VERSION, STATE, CORRELATION_ID, READY_AT, DECISION_ROUTE, " +
            "DECISION_ROUTE_KIND, DECISION_METHOD, GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION, CREATED_AT) " +
            "VALUES (:p_id, :p_idea, :p_iv, :p_pv, :p_state, :p_corr, :p_ready, :p_route, :p_kind, :p_method, :p_gid, :p_gver, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, p.PlanId);
        P(cmd, "p_idea", OracleDbType.Varchar2, p.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, p.IdeaVersion);
        P(cmd, "p_pv", OracleDbType.Int32, p.PlanVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, p.State);
        P(cmd, "p_corr", OracleDbType.Varchar2, p.CorrelationId);
        P(cmd, "p_ready", OracleDbType.TimeStampTZ, p.ReadyAt);
        P(cmd, "p_route", OracleDbType.Varchar2, Nz(p.DecisionRoute));
        P(cmd, "p_kind", OracleDbType.Varchar2, Nz(p.DecisionRouteKind));
        P(cmd, "p_method", OracleDbType.Varchar2, Nz(p.DecisionMethod));
        P(cmd, "p_gid", OracleDbType.Varchar2, Nz(p.GovernanceProfileId));
        P(cmd, "p_gver", OracleDbType.Varchar2, Nz(p.GovernanceProfileVersion));
        P(cmd, "p_at", OracleDbType.TimeStampTZ, p.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAssignmentAsync(OracleConnection conn, OracleTransaction tx, EvaluationAssignmentEnvelope a, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_EVALUATION_ASSIGNMENT (ID, PLAN_ID, IDEA_ID, IDEA_VERSION, ROLE_CODE, SCOPE_CODE, REQUIRED_FLAG, STATE, CORRELATION_ID, " +
            "ENTITY_VERSION, COMPLETED_AT, COMPLETED_BY_PERSON_ID, AUTHORITY_ASSIGNMENT_ID, ASSESSMENT_SCHEMA_ID, ASSESSMENT_SCHEMA_VERSION, ASSESSMENT_OUTCOME, CREATED_AT) " +
            "VALUES (:p_id, :p_plan, :p_idea, :p_iv, :p_role, :p_scope, :p_req, :p_state, :p_corr, :p_ver, :p_done, :p_by, :p_aid, :p_sid, :p_sver, :p_out, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, a.AssignmentId);
        P(cmd, "p_plan", OracleDbType.Varchar2, a.PlanId);
        P(cmd, "p_idea", OracleDbType.Varchar2, a.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, a.IdeaVersion);
        P(cmd, "p_role", OracleDbType.Varchar2, a.Role);
        P(cmd, "p_scope", OracleDbType.Varchar2, a.Scope);
        P(cmd, "p_req", OracleDbType.Int32, a.Required ? 1 : 0);
        P(cmd, "p_state", OracleDbType.Varchar2, a.State);
        P(cmd, "p_corr", OracleDbType.Varchar2, a.CorrelationId);
        P(cmd, "p_ver", OracleDbType.Int32, a.AssignmentVersion);
        P(cmd, "p_done", OracleDbType.TimeStampTZ, a.CompletedAt);
        P(cmd, "p_by", OracleDbType.Varchar2, Nz(a.CompletedByPersonId));
        P(cmd, "p_aid", OracleDbType.Varchar2, Nz(a.AuthorityAssignmentId));
        P(cmd, "p_sid", OracleDbType.Varchar2, Nz(a.AssessmentSchemaId));
        P(cmd, "p_sver", OracleDbType.Varchar2, Nz(a.AssessmentSchemaVersion));
        P(cmd, "p_out", OracleDbType.Varchar2, Nz(a.AssessmentOutcome));
        P(cmd, "p_at", OracleDbType.TimeStampTZ, a.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
