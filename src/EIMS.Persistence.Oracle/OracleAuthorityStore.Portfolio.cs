using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2f-1: Portfolio thread (candidate, assignment, membership, execution recommendation, execution hand-off) on Oracle.
// The thread's concurrency token is EIMS_PORTFOLIO_CANDIDATE.THREAD_VERSION; the candidate row is locked FOR UPDATE for every command.
// The hand-off inserts the shared EIMS_EXECUTION row (lifecycle columns defaulted); the Execution store (S2f-2) updates the same row.
public sealed partial class OracleAuthorityStore : IPortfolioWave9Store
{
    private const string EligibilityCommand = "portfolio.evaluate-eligibility-from-approved-idea";

    public PortfolioWave9PersistenceFaultPoint PortfolioFaultPoint { get; set; }

    private void ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint point)
    {
        if (PortfolioFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave9 Portfolio persistence fault at {point}.");
    }

    private static AggregateSnapshot ThreadCarrier(string aggregateId) =>
        new(aggregateId, "PORTFOLIO_THREAD", "N/A", 0, null, null, null, null, null, null);

    public async ValueTask<PortfolioThreadEnvelope?> GetByIdeaVersionAsync(
        string ideaId, long approvedIdeaVersion, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var candidateId = await FindCandidateIdAsync(conn, null, ideaId, approvedIdeaVersion, cancellationToken);
        return candidateId is null ? null : await ReadThreadAsync(conn, null, candidateId, false, cancellationToken);
    }

    public async ValueTask<PortfolioThreadEnvelope?> GetByCandidateAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadThreadAsync(conn, null, candidateId, false, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetPortfolioIdempotencyAsync(
        string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, commandName, aggregateId, idempotencyKey, cancellationToken);
    }

    public async ValueTask<AuthorityResult> CommitEligibilityAsync(
        PortfolioEligibilityRequestWave9 request, PortfolioCommitWave9 commit, CancellationToken cancellationToken = default)
    {
        var trigger = request.Trigger;
        var corr = trigger.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, EligibilityCommand, trigger.IdeaId, request.Fingerprint, cancellationToken);
            if (prior is not null)
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };

            var existingId = await FindCandidateIdAsync(conn, tx, trigger.IdeaId, trigger.ApprovedIdeaVersion, cancellationToken);
            if (existingId is not null)
            {
                var existing = await ReadThreadAsync(conn, tx, existingId, false, cancellationToken);
                return new AuthorityResult(
                    200, "P1_PORTFOLIO_ELIGIBILITY_ALREADY_MATERIALIZED", Allowed: true, StateMutated: false, IdempotentReplay: true,
                    NewVersion: existing?.ThreadVersion, CorrelationId: corr, EmittedEvents: Array.Empty<string>());
            }

            PortfolioTransactionalStoreWave9.ValidateEligibilityCommit(request, commit);

            var events = commit.OutboxEvents.ToArray();
            var result = new AuthorityResult(
                200, "P1_PORTFOLIO_CANDIDATE_CREATED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.After.ThreadVersion, CorrelationId: corr,
                EmittedEvents: events.Select(x => x.EventName).ToArray());
            var carrier = ThreadCarrier(commit.Audit.AggregateId);

            try
            {
                await InsertCandidateAsync(conn, tx, commit.After.Candidate, commit.After.ThreadVersion, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterThreadStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, EligibilityCommand, trigger.IdeaId, request.Fingerprint,
                    request.Fingerprint, result, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue; // concurrent same-fingerprint commit won the race: re-read it as replay
                if (Has(ex, "UQ_PORT_CAND_IDEA_VERSION") || Has(ex, "PK_PORTFOLIO_CANDIDATE"))
                    return AuthorityResult.Deny(409, "P1_PORTFOLIO_CONCURRENT_CONFLICT", corr);
                if (Has(ex, "UQ_EIMS_AUDIT_ID"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_OUTBOX_ID", corr);
                throw;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    public async ValueTask<AuthorityResult> CommitPortfolioCommandAsync(
        PortfolioCommandRequestWave9 request, PortfolioCommitWave9 commit, CancellationToken cancellationToken = default)
    {
        var command = request.Command;
        var corr = command.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, command.CommandName, command.CandidateId, command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var current = await ReadThreadAsync(conn, tx, command.CandidateId, true, cancellationToken);
            if (current is null
                || current.ThreadVersion != request.Before.ThreadVersion
                || current.ThreadVersion != command.ExpectedThreadVersion)
                return AuthorityResult.Deny(409, "P1_PORTFOLIO_CONCURRENT_CONFLICT", corr);

            PortfolioTransactionalStoreWave9.ValidateCommandCommit(request, commit);
            if ((current.Assignment is not null && commit.After.Assignment is null)
                || (current.Membership is not null && commit.After.Membership is null)
                || (current.Recommendation is not null && commit.After.Recommendation is null)
                || (current.Execution is not null && commit.After.Execution is null))
                throw new InvalidOperationException("P1_PORTFOLIO_THREAD_SHRINK_NOT_SUPPORTED");

            var events = commit.OutboxEvents.ToArray();
            var result = new AuthorityResult(
                200, "P1_PORTFOLIO_COMMAND_COMMITTED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.After.ThreadVersion, CorrelationId: corr,
                EmittedEvents: events.Select(x => x.EventName).ToArray());
            var carrier = ThreadCarrier(commit.Audit.AggregateId);
            var after = commit.After;

            try
            {
                await UpdateCandidateAsync(conn, tx, after.Candidate, after.ThreadVersion, current.ThreadVersion, cancellationToken);
                if (after.Assignment is not null)
                    await WriteAssignmentAsync(conn, tx, current.Assignment, after.Assignment, cancellationToken);
                if (after.Membership is not null)
                    await WriteMembershipAsync(conn, tx, current.Membership, after.Membership, cancellationToken);
                if (after.Recommendation is not null)
                    await WriteRecommendationAsync(conn, tx, current.Recommendation, after.Recommendation, cancellationToken);
                if (after.Execution is not null)
                    await WriteHandoffAsync(conn, tx, current.Execution, after.Execution, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterThreadStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, command.CommandName, command.CandidateId, command.IdempotencyKey,
                    request.Fingerprint, result, cancellationToken);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowPortfolioIf(PortfolioWave9PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue;
                if (Has(ex, "UQ_EIMS_AUDIT_ID"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_OUTBOX_ID", corr);
                return AuthorityResult.Deny(409, "P1_PORTFOLIO_CONCURRENT_CONFLICT", corr);
            }
            catch (ConflictException)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, "P1_PORTFOLIO_CONCURRENT_CONFLICT", corr);
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
    private static async Task<string?> FindCandidateIdAsync(
        OracleConnection conn, OracleTransaction? tx, string ideaId, long approvedIdeaVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, "SELECT ID FROM EIMS_PORTFOLIO_CANDIDATE WHERE IDEA_ID = :p_idea AND APPROVED_IDEA_VERSION = :p_ver");
        P(cmd, "p_idea", OracleDbType.Varchar2, ideaId);
        P(cmd, "p_ver", OracleDbType.Int64, approvedIdeaVersion);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToString(value);
    }


    private static async Task<PortfolioThreadEnvelope?> ReadThreadAsync(
        OracleConnection conn, OracleTransaction? tx, string candidateId, bool forUpdate, CancellationToken ct)
    {
        PortfolioCandidateEnvelope candidate;
        long threadVersion;
        await using (var cmd = Cmd(conn, tx,
            "SELECT ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, " + Ts("CREATED_AT") + ", CORRELATION_ID, THREAD_VERSION " +
            "FROM EIMS_PORTFOLIO_CANDIDATE WHERE ID = :p_id" + (forUpdate ? " FOR UPDATE" : "")))
        {
            P(cmd, "p_id", OracleDbType.Varchar2, candidateId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            candidate = new PortfolioCandidateEnvelope(r.GetString(0), r.GetString(1), Convert.ToInt64(r.GetValue(2)), r.GetString(3),
                Convert.ToInt64(r.GetValue(4)), ParseTs(r.GetString(5)), r.GetString(6));
            threadVersion = Convert.ToInt64(r.GetValue(7));
        }

        var assignment = await QueryOneAsync(conn, tx,
            "SELECT ID, CANDIDATE_ID, PORTFOLIO_ID, STATE, ENTITY_VERSION, " + Ts("CREATED_AT") + ", CORRELATION_ID " +
            "FROM EIMS_PORTFOLIO_ASSIGNMENT WHERE CANDIDATE_ID = :p_id",
            candidateId,
            r => new PortfolioAssignmentEnvelope(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Convert.ToInt64(r.GetValue(4)), ParseTs(r.GetString(5)), r.GetString(6)), ct);

        var membership = await QueryOneAsync(conn, tx,
            "SELECT ID, CANDIDATE_ID, ASSIGNMENT_ID, PORTFOLIO_ID, STATUS, ENTITY_VERSION, " + Ts("DECIDED_AT") +
            ", DECIDED_BY_PERSON_ID, AUTHORITY_ASSIGNMENT_ID, CORRELATION_ID FROM EIMS_PORTFOLIO_MEMBERSHIP WHERE CANDIDATE_ID = :p_id",
            candidateId,
            r => new PortfolioMembershipEnvelope(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                Convert.ToInt64(r.GetValue(5)), ParseTs(r.GetString(6)), r.GetString(7), r.GetString(8), r.GetString(9)), ct);

        var recommendation = await QueryOneAsync(conn, tx,
            "SELECT ID, CANDIDATE_ID, MEMBERSHIP_ID, STATE, ENTITY_VERSION, GOVERNANCE_DECISION_REF, BASELINE_APPROVED, " +
            "APPROVED_BASELINE_REF, " + Ts("CREATED_AT") + ", CORRELATION_ID FROM EIMS_EXECUTION_RECOMMENDATION WHERE CANDIDATE_ID = :p_id",
            candidateId,
            r => new ExecutionRecommendationEnvelope(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Convert.ToInt64(r.GetValue(4)), Str(r, 5), Convert.ToInt32(r.GetValue(6)) == 1, Str(r, 7),
                ParseTs(r.GetString(8)), r.GetString(9)), ct);

        var execution = await QueryOneAsync(conn, tx,
            "SELECT ID, RECOMMENDATION_ID, CANDIDATE_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, " + Ts("CREATED_AT") +
            ", CORRELATION_ID FROM EIMS_EXECUTION WHERE CANDIDATE_ID = :p_id",
            candidateId,
            r => new ExecutionHandoffEnvelope(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Convert.ToInt64(r.GetValue(4)), r.GetString(5), Convert.ToInt64(r.GetValue(6)), ParseTs(r.GetString(7)),
                r.GetString(8)), ct);

        return new PortfolioThreadEnvelope(candidate, assignment, membership, recommendation, execution, threadVersion);
    }

    // ---- writes
    private static async Task InsertCandidateAsync(
        OracleConnection conn, OracleTransaction tx, PortfolioCandidateEnvelope c, long threadVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_PORTFOLIO_CANDIDATE (ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, THREAD_VERSION, CORRELATION_ID, CREATED_AT) " +
            "VALUES (:p_id, :p_idea, :p_iv, :p_state, :p_ver, :p_tv, :p_corr, :p_at)");
        P(cmd, "p_id", OracleDbType.Varchar2, c.CandidateId);
        P(cmd, "p_idea", OracleDbType.Varchar2, c.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, c.ApprovedIdeaVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, c.State);
        P(cmd, "p_ver", OracleDbType.Int64, c.Version);
        P(cmd, "p_tv", OracleDbType.Int64, threadVersion);
        P(cmd, "p_corr", OracleDbType.Varchar2, c.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, c.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateCandidateAsync(
        OracleConnection conn, OracleTransaction tx, PortfolioCandidateEnvelope c, long newThreadVersion, long expectedThreadVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_PORTFOLIO_CANDIDATE SET STATE = :p_state, ENTITY_VERSION = :p_ver, THREAD_VERSION = :p_tv, CORRELATION_ID = :p_corr, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE ID = :p_id AND THREAD_VERSION = :p_expected");
        P(cmd, "p_state", OracleDbType.Varchar2, c.State);
        P(cmd, "p_ver", OracleDbType.Int64, c.Version);
        P(cmd, "p_tv", OracleDbType.Int64, newThreadVersion);
        P(cmd, "p_corr", OracleDbType.Varchar2, c.CorrelationId);
        P(cmd, "p_id", OracleDbType.Varchar2, c.CandidateId);
        P(cmd, "p_expected", OracleDbType.Int64, expectedThreadVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_PORTFOLIO_CONCURRENT_CONFLICT");
    }

    private static async Task WriteAssignmentAsync(
        OracleConnection conn, OracleTransaction tx, PortfolioAssignmentEnvelope? current, PortfolioAssignmentEnvelope a, CancellationToken ct)
    {
        if (current is not null && current == a) return;
        await using var cmd = Cmd(conn, tx, current is null
            ? "INSERT INTO EIMS_PORTFOLIO_ASSIGNMENT (ID, CANDIDATE_ID, PORTFOLIO_ID, STATE, ENTITY_VERSION, CORRELATION_ID, CREATED_AT) " +
              "VALUES (:p_id, :p_cand, :p_port, :p_state, :p_ver, :p_corr, :p_at)"
            : "UPDATE EIMS_PORTFOLIO_ASSIGNMENT SET PORTFOLIO_ID = :p_port, STATE = :p_state, ENTITY_VERSION = :p_ver, CORRELATION_ID = :p_corr, " +
              "UPDATED_AT = SYSTIMESTAMP WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_id", OracleDbType.Varchar2, a.AssignmentId);
        if (current is null) P(cmd, "p_cand", OracleDbType.Varchar2, a.CandidateId);
        P(cmd, "p_port", OracleDbType.Varchar2, a.PortfolioId);
        P(cmd, "p_state", OracleDbType.Varchar2, a.State);
        P(cmd, "p_ver", OracleDbType.Int64, a.Version);
        P(cmd, "p_corr", OracleDbType.Varchar2, a.CorrelationId);
        if (current is null) P(cmd, "p_at", OracleDbType.TimeStampTZ, a.CreatedAt);
        else P(cmd, "p_expected", OracleDbType.Int64, current.Version);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_PORTFOLIO_CONCURRENT_CONFLICT");
    }

    private static async Task WriteMembershipAsync(
        OracleConnection conn, OracleTransaction tx, PortfolioMembershipEnvelope? current, PortfolioMembershipEnvelope m, CancellationToken ct)
    {
        if (current is not null && current == m) return;
        await using var cmd = Cmd(conn, tx, current is null
            ? "INSERT INTO EIMS_PORTFOLIO_MEMBERSHIP (ID, CANDIDATE_ID, ASSIGNMENT_ID, PORTFOLIO_ID, STATUS, ENTITY_VERSION, DECIDED_BY_PERSON_ID, " +
              "AUTHORITY_ASSIGNMENT_ID, CORRELATION_ID, DECIDED_AT) VALUES (:p_id, :p_cand, :p_asg, :p_port, :p_status, :p_ver, :p_by, :p_aid, :p_corr, :p_at)"
            : "UPDATE EIMS_PORTFOLIO_MEMBERSHIP SET PORTFOLIO_ID = :p_port, STATUS = :p_status, ENTITY_VERSION = :p_ver, DECIDED_BY_PERSON_ID = :p_by, " +
              "AUTHORITY_ASSIGNMENT_ID = :p_aid, CORRELATION_ID = :p_corr, DECIDED_AT = :p_at, UPDATED_AT = SYSTIMESTAMP " +
              "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_id", OracleDbType.Varchar2, m.MembershipId);
        if (current is null)
        {
            P(cmd, "p_cand", OracleDbType.Varchar2, m.CandidateId);
            P(cmd, "p_asg", OracleDbType.Varchar2, m.AssignmentId);
        }
        P(cmd, "p_port", OracleDbType.Varchar2, m.PortfolioId);
        P(cmd, "p_status", OracleDbType.Varchar2, m.Status);
        P(cmd, "p_ver", OracleDbType.Int64, m.Version);
        P(cmd, "p_by", OracleDbType.Varchar2, m.DecidedByPersonId);
        P(cmd, "p_aid", OracleDbType.Varchar2, m.AuthorityAssignmentId);
        P(cmd, "p_corr", OracleDbType.Varchar2, m.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, m.DecidedAt);
        if (current is not null) P(cmd, "p_expected", OracleDbType.Int64, current.Version);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_PORTFOLIO_CONCURRENT_CONFLICT");
    }

    private static async Task WriteRecommendationAsync(
        OracleConnection conn, OracleTransaction tx, ExecutionRecommendationEnvelope? current, ExecutionRecommendationEnvelope r, CancellationToken ct)
    {
        if (current is not null && current == r) return;
        await using var cmd = Cmd(conn, tx, current is null
            ? "INSERT INTO EIMS_EXECUTION_RECOMMENDATION (ID, CANDIDATE_ID, MEMBERSHIP_ID, STATE, ENTITY_VERSION, GOVERNANCE_DECISION_REF, " +
              "BASELINE_APPROVED, APPROVED_BASELINE_REF, CORRELATION_ID, CREATED_AT) " +
              "VALUES (:p_id, :p_cand, :p_mem, :p_state, :p_ver, :p_gov, :p_base, :p_ref, :p_corr, :p_at)"
            : "UPDATE EIMS_EXECUTION_RECOMMENDATION SET STATE = :p_state, ENTITY_VERSION = :p_ver, GOVERNANCE_DECISION_REF = :p_gov, " +
              "BASELINE_APPROVED = :p_base, APPROVED_BASELINE_REF = :p_ref, CORRELATION_ID = :p_corr, UPDATED_AT = SYSTIMESTAMP " +
              "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_id", OracleDbType.Varchar2, r.RecommendationId);
        if (current is null)
        {
            P(cmd, "p_cand", OracleDbType.Varchar2, r.CandidateId);
            P(cmd, "p_mem", OracleDbType.Varchar2, r.MembershipId);
        }
        P(cmd, "p_state", OracleDbType.Varchar2, r.State);
        P(cmd, "p_ver", OracleDbType.Int64, r.Version);
        P(cmd, "p_gov", OracleDbType.Varchar2, Nz(r.GovernanceDecisionRef));
        P(cmd, "p_base", OracleDbType.Int32, r.BaselineApproved ? 1 : 0);
        P(cmd, "p_ref", OracleDbType.Varchar2, Nz(r.ApprovedBaselineRef));
        P(cmd, "p_corr", OracleDbType.Varchar2, r.CorrelationId);
        if (current is null) P(cmd, "p_at", OracleDbType.TimeStampTZ, r.CreatedAt);
        else P(cmd, "p_expected", OracleDbType.Int64, current.Version);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_PORTFOLIO_CONCURRENT_CONFLICT");
    }

    private static async Task WriteHandoffAsync(
        OracleConnection conn, OracleTransaction tx, ExecutionHandoffEnvelope? current, ExecutionHandoffEnvelope x, CancellationToken ct)
    {
        if (current is not null && current == x) return;
        await using var cmd = Cmd(conn, tx, current is null
            ? "INSERT INTO EIMS_EXECUTION (ID, RECOMMENDATION_ID, CANDIDATE_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, CORRELATION_ID, CREATED_AT) " +
              "VALUES (:p_id, :p_rec, :p_cand, :p_idea, :p_iv, :p_state, :p_ver, :p_corr, :p_at)"
            : "UPDATE EIMS_EXECUTION SET STATE = :p_state, ENTITY_VERSION = :p_ver, CORRELATION_ID = :p_corr, UPDATED_AT = SYSTIMESTAMP " +
              "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_id", OracleDbType.Varchar2, x.ExecutionId);
        if (current is null)
        {
            P(cmd, "p_rec", OracleDbType.Varchar2, x.RecommendationId);
            P(cmd, "p_cand", OracleDbType.Varchar2, x.CandidateId);
            P(cmd, "p_idea", OracleDbType.Varchar2, x.IdeaId);
            P(cmd, "p_iv", OracleDbType.Int64, x.ApprovedIdeaVersion);
        }
        P(cmd, "p_state", OracleDbType.Varchar2, x.State);
        P(cmd, "p_ver", OracleDbType.Int64, x.Version);
        P(cmd, "p_corr", OracleDbType.Varchar2, x.CorrelationId);
        if (current is null) P(cmd, "p_at", OracleDbType.TimeStampTZ, x.CreatedAt);
        else P(cmd, "p_expected", OracleDbType.Int64, current.Version);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_PORTFOLIO_CONCURRENT_CONFLICT");
    }
}

