using System.Data.Common;
using System.Text.Json;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2d: G04 committee voting on Oracle. One transaction: frozen committee snapshot (first vote) + append-only vote revision
// + effective-vote projection + committee state (optimistic COMMITTEE_VERSION) + audit + outbox + idempotency.
// Audit/outbox/idempotency of votes reference the G04 ASSESSMENT as the aggregate (same rule as the in-memory store).
public sealed partial class OracleAuthorityStore : IG04VotingStore
{
    private const string VoteCommand = "g04.vote";

    public G04VotingPersistenceFaultPoint VotingFaultPoint { get; set; }

    private void ThrowVotingIf(G04VotingPersistenceFaultPoint point)
    {
        if (VotingFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected G04 voting persistence failure at {point}.");
    }

    public async ValueTask<IdempotencyRecord?> GetVoteIdempotencyAsync(
        string assessmentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, VoteCommand, assessmentId, idempotencyKey, cancellationToken);
    }

    public async ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadCommitteeSnapshotAsync(conn, null, assessmentId, cancellationToken);
    }

    public async ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadCommitteeStateAsync(conn, null, assessmentId, cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<G04EffectiveVoteEnvelope>> GetEffectiveVotesAsync(
        string assessmentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadEffectiveVotesAsync(conn, null, assessmentId, cancellationToken);
    }

    public async ValueTask<AuthorityResult> CommitG04VoteAsync(
        G04VoteRequest request, G04VoteCommit commit, CancellationToken cancellationToken = default)
    {
        var corr = request.Command.CorrelationId;
        var assessmentId = request.Assessment.AssessmentId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var liveIdea = await ReadAggregateAsync(conn, tx, request.Idea.AggregateId, cancellationToken);
            // Lock the assessment row so concurrent votes on one assessment are serialized.
            var liveAssessment = await ReadG04ByPlanAsync(conn, tx, request.Command.PlanId, true, cancellationToken);
            if (liveIdea is null || liveAssessment is null)
                return AuthorityResult.Deny(404, "P2_G04_CONTEXT_NOT_FOUND", corr);
            if (liveIdea.Version != request.Idea.Version
                || liveIdea.Version != request.Command.ExpectedIdeaVersion
                || !string.Equals(liveAssessment.AssessmentId, assessmentId, StringComparison.Ordinal)
                || !string.Equals(liveAssessment.State, "PENDING", StringComparison.OrdinalIgnoreCase)
                || liveAssessment.IdeaVersion != liveIdea.Version)
                return AuthorityResult.Deny(409, "P2_G04_CONTEXT_VERSION_CONFLICT", corr);

            var prior = await ReadIdempotencyAsync(conn, tx, VoteCommand, assessmentId, request.Command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P2_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var currentSnapshot = await ReadCommitteeSnapshotAsync(conn, tx, assessmentId, cancellationToken);
            var currentState = await ReadCommitteeStateAsync(conn, tx, assessmentId, cancellationToken);
            if ((currentSnapshot is null) != (currentState is null))
                return AuthorityResult.Deny(500, "P2_G04_COMMITTEE_CONTEXT_INCONSISTENT", corr);

            var sameState = currentState is null ? request.StateBefore is null : request.StateBefore is not null && currentState == request.StateBefore;
            if (!G04VotingTransactionalStore.SameOptionalSnapshot(currentSnapshot, request.SnapshotBefore) || !sameState)
                return AuthorityResult.Deny(409, "P2_G04_COMMITTEE_CONTEXT_CHANGED", corr);

            if ((currentState?.CommitteeVersion ?? 0) != request.Command.ExpectedCommitteeVersion)
                return AuthorityResult.Deny(409, "P2_G04_COMMITTEE_VERSION_CONFLICT", corr);
            if (currentState is not null && string.Equals(currentState.VotingStageState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return AuthorityResult.Deny(409, "P2_G04_VOTING_STAGE_COMPLETED", corr);

            var currentEffective = (await ReadEffectiveVotesAsync(conn, tx, assessmentId, cancellationToken))
                .OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase).ToArray();
            var requestEffective = request.EffectiveVotesBefore.OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!currentEffective.SequenceEqual(requestEffective))
                return AuthorityResult.Deny(409, "P2_G04_EFFECTIVE_PROJECTION_CHANGED", corr);

            var shapeError = G04VotingTransactionalStore.ValidateCommitShape(request, commit, currentSnapshot, currentState, currentEffective);
            if (shapeError is not null)
                return AuthorityResult.Deny(500, shapeError, corr);

            var events = commit.OutboxEvents.ToArray();
            if (events.Length is < 1 or > 2
                || events.GroupBy(x => x.MessageId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                return AuthorityResult.Deny(409, "P2_G04_OUTBOX_INVALID", corr);

            var result = new AuthorityResult(
                200, "P2_G04_VOTE_ATOMIC_COMMIT", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.StateAfter.CommitteeVersion, CorrelationId: corr,
                EmittedEvents: Array.AsReadOnly(events.Select(x => x.EventName).ToArray()));
            var typeCarrier = liveIdea with { AggregateId = assessmentId, AggregateType = "G04_ASSESSMENT" };

            try
            {
                if (currentSnapshot is null)
                    await InsertCommitteeSnapshotAsync(conn, tx, commit.SnapshotAfter, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterSnapshotStaged);

                await InsertVoteRevisionAsync(conn, tx, commit.VoteRevision, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterVoteRevisionStaged);

                await UpsertEffectiveVoteAsync(conn, tx, commit.EffectiveVoteAfter, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterEffectiveProjectionStaged);

                if (currentState is null)
                    await InsertCommitteeStateAsync(conn, tx, commit.StateAfter, cancellationToken);
                else
                    await UpdateCommitteeStateAsync(conn, tx, commit.StateAfter, currentState.CommitteeVersion, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterCommitteeStateStaged);

                await InsertAuditAsync(conn, tx, typeCarrier, commit.Audit, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, typeCarrier, e, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, VoteCommand, assessmentId, request.Command.IdempotencyKey,
                    request.IdempotencyFingerprint, result, cancellationToken);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowVotingIf(G04VotingPersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue; // concurrent same-key commit won the race: re-read it as replay/conflict
                if (Has(ex, "PK_G04_VOTE_REVISION") || Has(ex, "UQ_G04_VOTE_REV_PERSON"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_G04_VOTE_REVISION_ID", corr);
                if (Has(ex, "UQ_EIMS_AUDIT_ID"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG"))
                    return AuthorityResult.Deny(409, "P2_G04_OUTBOX_INVALID", corr);
                if (Has(ex, "PK_G04_COMMITTEE_STATE") || Has(ex, "UQ_G04_COMMITTEE_SNAP_ASSESS") || Has(ex, "PK_G04_COMMITTEE_SNAPSHOT"))
                    return AuthorityResult.Deny(409, "P2_G04_COMMITTEE_VERSION_CONFLICT", corr);
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

    // ---- reads
    private static async Task<G04AssessmentEnvelope?> ReadG04ByPlanAsync(
        OracleConnection conn, OracleTransaction tx, string planId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, G04Select + " WHERE PLAN_ID = :p_id" + (forUpdate ? " FOR UPDATE" : ""));
        P(cmd, "p_id", OracleDbType.Varchar2, planId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadG04(r) : null;
    }

    private static readonly string CommitteeSnapshotSelect =
        "SELECT ID, ASSESSMENT_ID, IDEA_ID, IDEA_VERSION, SCOPE_CODE, DECISION_ROUTE, GOVERNANCE_PROFILE_ID, GOVERNANCE_PROFILE_VERSION, " +
        "QUORUM_REQUIRED, VOTE_RULE, CHAIR_PERSON_ID, APPROVAL_AUTHORITY, APPROVAL_REF, MEMBERS_JSON, " + Ts("CREATED_AT") +
        ", CORRELATION_ID FROM EIMS_G04_COMMITTEE_SNAPSHOT";

    private static readonly string CommitteeStateSelect =
        "SELECT ASSESSMENT_ID, SNAPSHOT_ID, COMMITTEE_VERSION, VOTING_STAGE_STATE, QUORUM_REACHED, APPROVAL_RULE_SATISFIED, " +
        "EFFECTIVE_VOTE_COUNT, APPROVE_COUNT, REJECT_COUNT, " + Ts("UPDATED_AT") + ", CORRELATION_ID, " + Ts("COMPLETED_AT") +
        " FROM EIMS_G04_COMMITTEE_STATE";

    private static async Task<G04CommitteeSnapshotEnvelope?> ReadCommitteeSnapshotAsync(
        OracleConnection conn, OracleTransaction? tx, string assessmentId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, CommitteeSnapshotSelect + " WHERE ASSESSMENT_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var members = JsonSerializer.Deserialize<List<G04CommitteeMemberEnvelope>>(r.GetString(13), Json)
            ?? throw new InvalidOperationException("Stored committee member list is empty.");
        return new G04CommitteeSnapshotEnvelope(
            r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToInt64(r.GetValue(3)), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), Convert.ToInt32(r.GetValue(8)), r.GetString(9), r.GetString(10),
            Str(r, 11) ?? string.Empty, Str(r, 12) ?? string.Empty, Array.AsReadOnly(members.ToArray()),
            ParseTs(r.GetString(14)), r.GetString(15));
    }

    private static async Task<G04CommitteeStateEnvelope?> ReadCommitteeStateAsync(
        OracleConnection conn, OracleTransaction? tx, string assessmentId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, CommitteeStateSelect + " WHERE ASSESSMENT_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new G04CommitteeStateEnvelope(
            r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2)), r.GetString(3),
            Convert.ToInt32(r.GetValue(4)) == 1, Convert.ToInt32(r.GetValue(5)) == 1,
            Convert.ToInt32(r.GetValue(6)), Convert.ToInt32(r.GetValue(7)), Convert.ToInt32(r.GetValue(8)),
            ParseTs(r.GetString(9)), r.GetString(10), r.IsDBNull(11) ? null : ParseTs(r.GetString(11)));
    }

    private static async Task<IReadOnlyCollection<G04EffectiveVoteEnvelope>> ReadEffectiveVotesAsync(
        OracleConnection conn, OracleTransaction? tx, string assessmentId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "SELECT ASSESSMENT_ID, PERSON_ID, VOTE_REVISION_ID, VOTE, REVISION_NUMBER, " + Ts("UPDATED_AT") +
            " FROM EIMS_G04_EFFECTIVE_VOTE WHERE ASSESSMENT_ID = :p_id ORDER BY PERSON_ID");
        P(cmd, "p_id", OracleDbType.Varchar2, assessmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<G04EffectiveVoteEnvelope>();
        while (await r.ReadAsync(ct))
            list.Add(new G04EffectiveVoteEnvelope(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Convert.ToInt32(r.GetValue(4)), ParseTs(r.GetString(5))));
        return list;
    }

    // ---- writes
    private static async Task InsertCommitteeSnapshotAsync(OracleConnection conn, OracleTransaction tx, G04CommitteeSnapshotEnvelope s, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_COMMITTEE_SNAPSHOT (ID, ASSESSMENT_ID, IDEA_ID, IDEA_VERSION, SCOPE_CODE, DECISION_ROUTE, GOVERNANCE_PROFILE_ID, " +
            "GOVERNANCE_PROFILE_VERSION, QUORUM_REQUIRED, VOTE_RULE, CHAIR_PERSON_ID, APPROVAL_AUTHORITY, APPROVAL_REF, MEMBERS_JSON, CORRELATION_ID, CREATED_AT) " +
            "VALUES (:p_id, :p_aid, :p_idea, :p_iv, :p_scope, :p_route, :p_gid, :p_gver, :p_quorum, :p_rule, :p_chair, :p_auth, :p_ref, :p_members, :p_corr, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, s.SnapshotId);
        P(cmd, "p_aid", OracleDbType.Varchar2, s.AssessmentId);
        P(cmd, "p_idea", OracleDbType.Varchar2, s.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, s.IdeaVersion);
        P(cmd, "p_scope", OracleDbType.Varchar2, s.Scope);
        P(cmd, "p_route", OracleDbType.Varchar2, s.DecisionRoute);
        P(cmd, "p_gid", OracleDbType.Varchar2, s.GovernanceProfileId);
        P(cmd, "p_gver", OracleDbType.Varchar2, s.GovernanceProfileVersion);
        P(cmd, "p_quorum", OracleDbType.Int32, s.QuorumRequired);
        P(cmd, "p_rule", OracleDbType.Varchar2, s.VoteRule);
        P(cmd, "p_chair", OracleDbType.Varchar2, s.ChairPersonId);
        P(cmd, "p_auth", OracleDbType.Varchar2, Nz(s.ApprovalAuthority));
        P(cmd, "p_ref", OracleDbType.Varchar2, Nz(s.ApprovalRef));
        P(cmd, "p_members", OracleDbType.Clob, JsonSerializer.Serialize(s.Members));
        P(cmd, "p_corr", OracleDbType.Varchar2, s.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, s.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertVoteRevisionAsync(OracleConnection conn, OracleTransaction tx, G04VoteRevisionEnvelope v, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_VOTE_REVISION (ID, ASSESSMENT_ID, SNAPSHOT_ID, COMMITTEE_VERSION, PERSON_ID, AUTHORITY_ASSIGNMENT_ID, VOTE, NOTE, " +
            "REVISION_NUMBER, SUPERSEDES_REVISION_ID, CORRELATION_ID, CREATED_AT) " +
            "VALUES (:p_id, :p_aid, :p_snap, :p_cv, :p_person, :p_asg, :p_vote, :p_note, :p_no, :p_sup, :p_corr, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, v.VoteRevisionId);
        P(cmd, "p_aid", OracleDbType.Varchar2, v.AssessmentId);
        P(cmd, "p_snap", OracleDbType.Varchar2, v.SnapshotId);
        P(cmd, "p_cv", OracleDbType.Int32, v.CommitteeVersion);
        P(cmd, "p_person", OracleDbType.Varchar2, v.PersonId);
        P(cmd, "p_asg", OracleDbType.Varchar2, v.AuthorityAssignmentId);
        P(cmd, "p_vote", OracleDbType.Varchar2, v.Vote);
        P(cmd, "p_note", OracleDbType.Varchar2, v.Note);
        P(cmd, "p_no", OracleDbType.Int32, v.RevisionNumber);
        P(cmd, "p_sup", OracleDbType.Varchar2, Nz(v.SupersedesVoteRevisionId));
        P(cmd, "p_corr", OracleDbType.Varchar2, v.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, v.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertEffectiveVoteAsync(OracleConnection conn, OracleTransaction tx, G04EffectiveVoteEnvelope e, CancellationToken ct)
    {
        await using (var upd = Cmd(conn, tx,
            "UPDATE EIMS_G04_EFFECTIVE_VOTE SET VOTE_REVISION_ID = :p_rev, VOTE = :p_vote, REVISION_NUMBER = :p_no, UPDATED_AT = :p_at " +
            "WHERE ASSESSMENT_ID = :p_aid AND PERSON_ID = :p_person"))
        {
            P(upd, "p_rev", OracleDbType.Varchar2, e.VoteRevisionId);
            P(upd, "p_vote", OracleDbType.Varchar2, e.Vote);
            P(upd, "p_no", OracleDbType.Int32, e.RevisionNumber);
            P(upd, "p_at", OracleDbType.TimeStampTZ, e.UpdatedAt);
            P(upd, "p_aid", OracleDbType.Varchar2, e.AssessmentId);
            P(upd, "p_person", OracleDbType.Varchar2, e.PersonId);
            if (await upd.ExecuteNonQueryAsync(ct) > 0) return;
        }

        await using var ins = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_EFFECTIVE_VOTE (ASSESSMENT_ID, PERSON_ID, VOTE_REVISION_ID, VOTE, REVISION_NUMBER, UPDATED_AT) " +
            "VALUES (:p_aid, :p_person, :p_rev, :p_vote, :p_no, :p_at)");
        P(ins, "p_aid", OracleDbType.Varchar2, e.AssessmentId);
        P(ins, "p_person", OracleDbType.Varchar2, e.PersonId);
        P(ins, "p_rev", OracleDbType.Varchar2, e.VoteRevisionId);
        P(ins, "p_vote", OracleDbType.Varchar2, e.Vote);
        P(ins, "p_no", OracleDbType.Int32, e.RevisionNumber);
        P(ins, "p_at", OracleDbType.TimeStampTZ, e.UpdatedAt);
        await ins.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertCommitteeStateAsync(OracleConnection conn, OracleTransaction tx, G04CommitteeStateEnvelope s, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_G04_COMMITTEE_STATE (ASSESSMENT_ID, SNAPSHOT_ID, COMMITTEE_VERSION, VOTING_STAGE_STATE, QUORUM_REACHED, " +
            "APPROVAL_RULE_SATISFIED, EFFECTIVE_VOTE_COUNT, APPROVE_COUNT, REJECT_COUNT, CORRELATION_ID, COMPLETED_AT, UPDATED_AT) " +
            "VALUES (:p_aid, :p_snap, :p_ver, :p_stage, :p_quorum, :p_appr, :p_eff, :p_ap, :p_rej, :p_corr, :p_done, :p_at)");
        BindCommitteeState(cmd, s);
        P(cmd, "p_aid", OracleDbType.Varchar2, s.AssessmentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateCommitteeStateAsync(
        OracleConnection conn, OracleTransaction tx, G04CommitteeStateEnvelope s, int expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_G04_COMMITTEE_STATE SET SNAPSHOT_ID = :p_snap, COMMITTEE_VERSION = :p_ver, VOTING_STAGE_STATE = :p_stage, " +
            "QUORUM_REACHED = :p_quorum, APPROVAL_RULE_SATISFIED = :p_appr, EFFECTIVE_VOTE_COUNT = :p_eff, APPROVE_COUNT = :p_ap, " +
            "REJECT_COUNT = :p_rej, CORRELATION_ID = :p_corr, COMPLETED_AT = :p_done, UPDATED_AT = :p_at " +
            "WHERE ASSESSMENT_ID = :p_aid AND COMMITTEE_VERSION = :p_expected");
        BindCommitteeState(cmd, s);
        P(cmd, "p_aid", OracleDbType.Varchar2, s.AssessmentId);
        P(cmd, "p_expected", OracleDbType.Int32, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new ConflictException("P2_G04_COMMITTEE_VERSION_CONFLICT");
    }

    private static void BindCommitteeState(OracleCommand cmd, G04CommitteeStateEnvelope s)
    {
        P(cmd, "p_snap", OracleDbType.Varchar2, s.SnapshotId);
        P(cmd, "p_ver", OracleDbType.Int32, s.CommitteeVersion);
        P(cmd, "p_stage", OracleDbType.Varchar2, s.VotingStageState);
        P(cmd, "p_quorum", OracleDbType.Int32, s.QuorumReached ? 1 : 0);
        P(cmd, "p_appr", OracleDbType.Int32, s.ApprovalRuleSatisfied ? 1 : 0);
        P(cmd, "p_eff", OracleDbType.Int32, s.EffectiveVoteCount);
        P(cmd, "p_ap", OracleDbType.Int32, s.ApproveCount);
        P(cmd, "p_rej", OracleDbType.Int32, s.RejectCount);
        P(cmd, "p_corr", OracleDbType.Varchar2, s.CorrelationId);
        P(cmd, "p_done", OracleDbType.TimeStampTZ, s.CompletedAt);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, s.UpdatedAt);
    }
}
