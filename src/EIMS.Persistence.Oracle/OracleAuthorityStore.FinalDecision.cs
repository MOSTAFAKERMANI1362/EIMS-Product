using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2e: G04 final decision on Oracle. One transaction: idea UPDATE (technical state-mutation version + revision predicates)
// + plan UPDATE + assessment UPDATE (decision-version predicate) + immutable 41-field evidence INSERT + audit + outbox + idempotency.
// The assessment and plan rows are locked FOR UPDATE, so two decisions on one assessment are serialized.
public sealed partial class OracleAuthorityStore : IG04FinalDecisionStore
{
    private const string FinalDecisionCommand = "g04.final-decision";

    public G04FinalDecisionPersistenceFaultPoint FinalDecisionFaultPoint { get; set; }

    private void ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint point)
    {
        if (FinalDecisionFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected G04 final-decision persistence failure at {point}.");
    }

    // ---- IG04FinalDecisionStore reads (committee snapshot/state reads are shared with the voting store)
    public ValueTask<AggregateSnapshot?> GetIdeaAsync(string ideaId, CancellationToken cancellationToken = default) =>
        GetAggregateAsync(ideaId, cancellationToken);

    public ValueTask<EvaluationPlanEnvelope?> GetPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        GetEvaluationPlanAsync(planId, cancellationToken);

    public async ValueTask<G04AssessmentEnvelope?> GetAssessmentAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadG04ByIdAsync(conn, null, assessmentId, false, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetFinalDecisionIdempotencyAsync(
        string assessmentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var assessment = await ReadG04ByIdAsync(conn, null, assessmentId, false, cancellationToken);
        return assessment is null
            ? null
            : await ReadIdempotencyAsync(conn, null, FinalDecisionCommand, assessment.IdeaId, idempotencyKey, cancellationToken);
    }

    /// <summary>Evidence read: the immutable final-decision record of one assessment.</summary>
    public async Task<G04FinalDecisionEvidenceEnvelope?> GetFinalDecisionEvidenceAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, FinalEvidenceSelect + " WHERE ASSESSMENT_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadFinalEvidence(r) : null;
    }

    public async Task<int> CountFinalDecisionEvidenceAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, "SELECT COUNT(*) FROM EIMS_G04_FINAL_DECISION WHERE ASSESSMENT_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async ValueTask<AuthorityResult> CommitFinalDecisionAsync(
        G04FinalDecisionRequest request, G04FinalDecisionCommit commit, CancellationToken cancellationToken = default)
    {
        var corr = request.Command.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, FinalDecisionCommand, request.Idea.AggregateId,
                request.Command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var assessment = await ReadG04ByIdAsync(conn, tx, request.Assessment.AssessmentId, true, cancellationToken);
            var plan = await ReadPlanAsync(conn, tx, request.Plan.PlanId, true, cancellationToken);
            var idea = await ReadAggregateAsync(conn, tx, request.Idea.AggregateId, cancellationToken);
            if (!MatchesCurrentFinalRequest(request, idea, plan, assessment))
                return AuthorityResult.Deny(409, "P1_G04_FINAL_CONCURRENT_CONFLICT", corr);

            // Same rules as the in-memory store (throws InvalidOperationException with the P1_G04_FINAL_* code, before any write).
            G04FinalDecisionTransactionalStore.ValidateCommit(request, commit);

            var result = new AuthorityResult(
                200, "P1_G04_FINAL_DECISION_COMMITTED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.IdeaAfter.Version, CorrelationId: corr,
                EmittedEvents: new[] { commit.Outbox.EventName });
            var expectedTechnical = request.Idea.StateMutationVersion ?? request.Idea.Version;

            try
            {
                await UpdateIdeaForFinalDecisionAsync(conn, tx, commit.IdeaAfter, request.Idea.Version, expectedTechnical, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterIdeaStaged);

                await UpdatePlanReadinessAsync(conn, tx, commit.PlanAfter, request.Plan.PlanVersion, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterPlanStaged);

                await UpdateAssessmentDecisionAsync(conn, tx, commit.AssessmentAfter, request.Assessment.DecisionVersion, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterAssessmentStaged);

                await InsertFinalEvidenceAsync(conn, tx, commit.Evidence, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterEvidenceStaged);

                await InsertAuditAsync(conn, tx, idea!, commit.Audit, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterAuditStaged);

                await InsertOutboxAsync(conn, tx, idea!, commit.Outbox, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, FinalDecisionCommand, request.Idea.AggregateId,
                    request.Command.IdempotencyKey, request.IdempotencyFingerprint, result, cancellationToken);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowFinalIf(G04FinalDecisionPersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue; // concurrent same-key commit won the race: re-read it as replay/conflict
                if (Has(ex, "PK_G04_FINAL_DECISION") || Has(ex, "UQ_G04_FINAL_DECISION_ASSESS"))
                    return AuthorityResult.Deny(409, "P1_G04_FINAL_CONCURRENT_CONFLICT", corr);
                if (Has(ex, "UQ_EIMS_AUDIT_ID"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_OUTBOX_ID", corr);
                throw;
            }
            catch (ConflictException)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, "P1_G04_FINAL_CONCURRENT_CONFLICT", corr);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    private static bool MatchesCurrentFinalRequest(
        G04FinalDecisionRequest r, AggregateSnapshot? idea, EvaluationPlanEnvelope? plan, G04AssessmentEnvelope? assessment)
    {
        if (idea is null || plan is null || assessment is null) return false;
        var currentTechnical = idea.StateMutationVersion ?? idea.Version;
        var requestTechnical = r.Idea.StateMutationVersion ?? r.Idea.Version;
        return string.Equals(idea.AggregateId, r.Idea.AggregateId, StringComparison.Ordinal)
            && idea.Version == r.Idea.Version
            && currentTechnical == requestTechnical
            && string.Equals(idea.State, r.Idea.State, StringComparison.Ordinal)
            && string.Equals(plan.PlanId, r.Plan.PlanId, StringComparison.Ordinal)
            && plan.PlanVersion == r.Plan.PlanVersion
            && string.Equals(plan.State, r.Plan.State, StringComparison.Ordinal)
            && string.Equals(assessment.AssessmentId, r.Assessment.AssessmentId, StringComparison.Ordinal)
            && assessment.DecisionVersion == r.Assessment.DecisionVersion
            && string.Equals(assessment.State, r.Assessment.State, StringComparison.Ordinal);
    }

    // ---- reads
    private static async Task<G04AssessmentEnvelope?> ReadG04ByIdAsync(
        OracleConnection conn, OracleTransaction? tx, string assessmentId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, G04Select + " WHERE ID = :p_id" + (forUpdate ? " FOR UPDATE" : ""));
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadG04(r) : null;
    }

    private static readonly string FinalEvidenceSelect =
        "SELECT ID, ASSESSMENT_ID, PLAN_ID, IDEA_ID, IDEA_REVISION_BEFORE, IDEA_REVISION_AFTER, STATE_MUTATION_BEFORE, STATE_MUTATION_AFTER, " +
        "DECISION_VERSION_BEFORE, DECISION_VERSION_AFTER, OUTCOME, REASON_CODE, DECISION_COMMENT, " + Ts("REVIEW_DATE") +
        ", PERSON_ID, AUTHORITY_ASSIGNMENT_ID, NETWORK_IDENTITY, IDENTITY_SOURCE, DECISION_ROUTE, DECISION_ROUTE_KIND, DECISION_METHOD, " +
        "GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION, DECISION_PROFILE_ID, DECISION_PROFILE_VERSION, PROFILE_SNAPSHOT_JSON, " +
        "PROFILE_SNAPSHOT_SHA256, RULE_SET, HARD_CONDITION_MAPPING_VERSION, GATE_SCORE, DATA_READINESS, RULE_CHECK_RESULTS, " +
        "SPECIALIST_OUTCOME_SUMMARY, COMMITTEE_SNAPSHOT_ID, COMMITTEE_VERSION, COMMITTEE_STAGE_STATE, COMMITTEE_APPROVAL_SATISFIED, " +
        "SOD_RESULT, SOD_EXCEPTION_POLICY, " + Ts("DECIDED_AT") + ", CORRELATION_ID FROM EIMS_G04_FINAL_DECISION";

    private static G04FinalDecisionEvidenceEnvelope ReadFinalEvidence(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        Convert.ToInt64(r.GetValue(4)), Convert.ToInt64(r.GetValue(5)), Convert.ToInt64(r.GetValue(6)), Convert.ToInt64(r.GetValue(7)),
        Convert.ToInt32(r.GetValue(8)), Convert.ToInt32(r.GetValue(9)),
        r.GetString(10), r.GetString(11), Str(r, 12) ?? string.Empty, r.IsDBNull(13) ? null : ParseTs(r.GetString(13)),
        r.GetString(14), r.GetString(15), r.GetString(16), r.GetString(17), r.GetString(18), r.GetString(19), r.GetString(20),
        r.GetString(21), r.GetString(22), r.GetString(23), r.GetString(24), Str(r, 25) ?? string.Empty, r.GetString(26),
        r.GetString(27), r.GetString(28), Convert.ToInt32(r.GetValue(29)), Convert.ToInt32(r.GetValue(30)),
        Str(r, 31) ?? string.Empty, Str(r, 32) ?? string.Empty, Str(r, 33),
        r.IsDBNull(34) ? null : Convert.ToInt32(r.GetValue(34)), Str(r, 35),
        r.IsDBNull(36) ? null : Convert.ToInt32(r.GetValue(36)) == 1,
        r.GetString(37), Str(r, 38), ParseTs(r.GetString(39)), r.GetString(40));

    // ---- writes
    private static async Task UpdateIdeaForFinalDecisionAsync(
        OracleConnection conn, OracleTransaction tx, AggregateSnapshot a, long expectedRevision, long expectedTechnical, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_AGGREGATE SET AGG_STATE = :p_state, ENTITY_VERSION = :p_ver, OWNER_PERSON_ID = :p_owner, OWNER_ROLE = :p_role, " +
            "SCOPE_CODE = :p_scope, WORK_ROUTING_ROLE = :p_route, STATE_MUTATION_VERSION = :p_smv, RULE_FACTS_JSON = :p_facts, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE AGGREGATE_ID = :p_id AND ENTITY_VERSION = :p_exp_rev AND NVL(STATE_MUTATION_VERSION, ENTITY_VERSION) = :p_exp_tech");
        P(cmd, "p_state", OracleDbType.Varchar2, a.State);
        P(cmd, "p_ver", OracleDbType.Int64, a.Version);
        P(cmd, "p_owner", OracleDbType.Varchar2, Nz(a.OwnerPersonId));
        P(cmd, "p_role", OracleDbType.Varchar2, Nz(a.OwnerRole));
        P(cmd, "p_scope", OracleDbType.Varchar2, Nz(a.Scope));
        P(cmd, "p_route", OracleDbType.Varchar2, Nz(a.WorkRoutingRole));
        P(cmd, "p_smv", OracleDbType.Int64, a.StateMutationVersion);
        P(cmd, "p_facts", OracleDbType.Clob, ToJson(a.RuleFacts));
        P(cmd, "p_id", OracleDbType.Varchar2, a.AggregateId);
        P(cmd, "p_exp_rev", OracleDbType.Int64, expectedRevision);
        P(cmd, "p_exp_tech", OracleDbType.Int64, expectedTechnical);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new ConflictException("P1_G04_FINAL_CONCURRENT_CONFLICT");
    }

    private static async Task UpdateAssessmentDecisionAsync(
        OracleConnection conn, OracleTransaction tx, G04AssessmentEnvelope g, int expectedDecisionVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_G04_ASSESSMENT SET PLAN_VERSION = :p_pv, STATE = :p_state, REQUIRED_SNAPSHOT_SHA256 = :p_sha, CORRELATION_ID = :p_corr, " +
            "DECISION_ROUTE = :p_route, DECISION_ROUTE_KIND = :p_kind, DECISION_METHOD = :p_method, GOVERNANCE_PROFILE_ID = :p_gid, " +
            "GOVERNANCE_PROFILE_VERSION = :p_gver, DECISION_VERSION = :p_dv, ENTITY_VERSION = ENTITY_VERSION + 1, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE ID = :p_id AND DECISION_VERSION = :p_expected");
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
        P(cmd, "p_id", OracleDbType.Varchar2, g.AssessmentId);
        P(cmd, "p_expected", OracleDbType.Int32, expectedDecisionVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new ConflictException("P1_G04_FINAL_CONCURRENT_CONFLICT");
    }

    private static async Task InsertFinalEvidenceAsync(OracleConnection conn, OracleTransaction tx, G04FinalDecisionEvidenceEnvelope e, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_FINAL_DECISION (ID, ASSESSMENT_ID, PLAN_ID, IDEA_ID, IDEA_REVISION_BEFORE, IDEA_REVISION_AFTER, STATE_MUTATION_BEFORE, " +
            "STATE_MUTATION_AFTER, DECISION_VERSION_BEFORE, DECISION_VERSION_AFTER, OUTCOME, REASON_CODE, DECISION_COMMENT, REVIEW_DATE, PERSON_ID, " +
            "AUTHORITY_ASSIGNMENT_ID, NETWORK_IDENTITY, IDENTITY_SOURCE, DECISION_ROUTE, DECISION_ROUTE_KIND, DECISION_METHOD, GOVERNANCE_PROFILE_ID, " +
            "GOVERNANCE_PROFILE_VERSION, DECISION_PROFILE_ID, DECISION_PROFILE_VERSION, PROFILE_SNAPSHOT_JSON, PROFILE_SNAPSHOT_SHA256, RULE_SET, " +
            "HARD_CONDITION_MAPPING_VERSION, GATE_SCORE, DATA_READINESS, RULE_CHECK_RESULTS, SPECIALIST_OUTCOME_SUMMARY, COMMITTEE_SNAPSHOT_ID, " +
            "COMMITTEE_VERSION, COMMITTEE_STAGE_STATE, COMMITTEE_APPROVAL_SATISFIED, SOD_RESULT, SOD_EXCEPTION_POLICY, DECIDED_AT, CORRELATION_ID) " +
            "VALUES (:p_id, :p_aid, :p_plan, :p_idea, :p_irb, :p_ira, :p_smb, :p_sma, :p_dvb, :p_dva, :p_out, :p_reason, :p_comment, :p_review, :p_person, " +
            ":p_asg, :p_net, :p_src, :p_route, :p_kind, :p_method, :p_gid, :p_gver, :p_dpid, :p_dpver, :p_pjson, :p_psha, :p_rule, :p_hcm, :p_gate, :p_ready, " +
            ":p_checks, :p_spec, :p_csnap, :p_cver, :p_cstage, :p_capp, :p_sod, :p_sodex, :p_at, :p_corr)");
        P(cmd, "p_id", OracleDbType.Varchar2, e.DecisionId);
        P(cmd, "p_aid", OracleDbType.Varchar2, e.AssessmentId);
        P(cmd, "p_plan", OracleDbType.Varchar2, e.PlanId);
        P(cmd, "p_idea", OracleDbType.Varchar2, e.IdeaId);
        P(cmd, "p_irb", OracleDbType.Int64, e.IdeaRevisionBefore);
        P(cmd, "p_ira", OracleDbType.Int64, e.IdeaRevisionAfter);
        P(cmd, "p_smb", OracleDbType.Int64, e.StateMutationVersionBefore);
        P(cmd, "p_sma", OracleDbType.Int64, e.StateMutationVersionAfter);
        P(cmd, "p_dvb", OracleDbType.Int32, e.AssessmentDecisionVersionBefore);
        P(cmd, "p_dva", OracleDbType.Int32, e.AssessmentDecisionVersionAfter);
        P(cmd, "p_out", OracleDbType.Varchar2, e.Outcome);
        P(cmd, "p_reason", OracleDbType.Varchar2, e.ReasonCode);
        P(cmd, "p_comment", OracleDbType.Clob, Nz(e.DecisionComment));
        P(cmd, "p_review", OracleDbType.TimeStampTZ, e.ReviewDate);
        P(cmd, "p_person", OracleDbType.Varchar2, e.PersonId);
        P(cmd, "p_asg", OracleDbType.Varchar2, e.AuthorityAssignmentId);
        P(cmd, "p_net", OracleDbType.Varchar2, e.NetworkIdentity);
        P(cmd, "p_src", OracleDbType.Varchar2, e.IdentitySource);
        P(cmd, "p_route", OracleDbType.Varchar2, e.DecisionRoute);
        P(cmd, "p_kind", OracleDbType.Varchar2, e.DecisionRouteKind);
        P(cmd, "p_method", OracleDbType.Varchar2, e.DecisionMethod);
        P(cmd, "p_gid", OracleDbType.Varchar2, e.GovernanceProfileId);
        P(cmd, "p_gver", OracleDbType.Varchar2, e.GovernanceProfileVersion);
        P(cmd, "p_dpid", OracleDbType.Varchar2, e.DecisionProfileId);
        P(cmd, "p_dpver", OracleDbType.Varchar2, e.DecisionProfileVersion);
        P(cmd, "p_pjson", OracleDbType.Clob, Nz(e.ProfileSnapshotJson));
        P(cmd, "p_psha", OracleDbType.Varchar2, e.ProfileSnapshotSha256);
        P(cmd, "p_rule", OracleDbType.Varchar2, e.RuleSet);
        P(cmd, "p_hcm", OracleDbType.Varchar2, e.HardConditionMappingVersion);
        P(cmd, "p_gate", OracleDbType.Int32, e.GateScore);
        P(cmd, "p_ready", OracleDbType.Int32, e.DataReadiness);
        P(cmd, "p_checks", OracleDbType.Clob, Nz(e.RuleCheckResults));
        P(cmd, "p_spec", OracleDbType.Clob, Nz(e.SpecialistOutcomeSummary));
        P(cmd, "p_csnap", OracleDbType.Varchar2, Nz(e.CommitteeSnapshotId));
        P(cmd, "p_cver", OracleDbType.Int32, e.CommitteeVersion);
        P(cmd, "p_cstage", OracleDbType.Varchar2, Nz(e.CommitteeStageState));
        object? committeeApproval = e.CommitteeApprovalRuleSatisfied.HasValue ? (object)(e.CommitteeApprovalRuleSatisfied.Value ? 1 : 0) : null;
        P(cmd, "p_capp", OracleDbType.Int32, committeeApproval);
        P(cmd, "p_sod", OracleDbType.Varchar2, e.SodResult);
        P(cmd, "p_sodex", OracleDbType.Varchar2, Nz(e.SodExceptionPolicy));
        P(cmd, "p_at", OracleDbType.TimeStampTZ, e.Timestamp);
        P(cmd, "p_corr", OracleDbType.Varchar2, e.CorrelationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
