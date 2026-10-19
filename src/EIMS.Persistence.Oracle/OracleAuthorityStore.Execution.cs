using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2f-2: Wave 10 Execution lifecycle on the shared EIMS_EXECUTION row (inserted by the portfolio hand-off, S2f-1).
// One transaction: execution UPDATE (version predicate) + optional benefit-obligation INSERT into the shared EIMS_BENEFIT row
// + audit + outbox + idempotency. The execution row is locked FOR UPDATE for every command.
// NOTE: the system-only hand-off INTAKE store (IExecutionHandoffIntakeStoreWave10) is not bound here; with the shared row the
// hand-off already materializes the execution (see ACR-S2f2, follow-up S2f-2b).
public sealed partial class OracleAuthorityStore : IExecutionWave10Store
{
    public ExecutionWave10PersistenceFaultPoint ExecutionFaultPoint { get; set; }

    private void ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint point)
    {
        if (ExecutionFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave10 Execution persistence fault at {point}.");
    }

    private static AggregateSnapshot ExecutionCarrier(string aggregateId) =>
        new(aggregateId, "EXECUTION", "N/A", 0, null, null, null, null, null, null);

    public async ValueTask<ExecutionThreadEnvelopeWave10?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadExecutionThreadAsync(conn, null, executionId, false, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetExecutionIdempotencyAsync(
        string commandName, string executionId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, commandName, executionId, idempotencyKey, cancellationToken);
    }

    public async ValueTask<AuthorityResult> CommitExecutionCommandAsync(
        ExecutionCommandRequestWave10 request, ExecutionCommitWave10 commit, CancellationToken cancellationToken = default)
    {
        var command = request.Command;
        var corr = command.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, command.CommandName, command.ExecutionId, command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var current = await ReadExecutionThreadAsync(conn, tx, command.ExecutionId, true, cancellationToken);
            if (current is null
                || current.Execution.Version != request.Before.Execution.Version
                || current.Execution.Version != command.ExpectedVersion)
                return AuthorityResult.Deny(409, "P1_EXECUTION_CONCURRENT_CONFLICT", corr);

            ExecutionTransactionalStoreWave10.ValidateCommit(request, commit);
            if (current.BenefitObligation is not null && commit.After.BenefitObligation is null)
                throw new InvalidOperationException("P1_EXECUTION_THREAD_SHRINK_NOT_SUPPORTED");

            var events = commit.OutboxEvents.ToArray();
            var result = new AuthorityResult(
                200, "P1_EXECUTION_COMMAND_COMMITTED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.After.Execution.Version, CorrelationId: corr,
                EmittedEvents: events.Select(x => x.EventName).ToArray());
            var carrier = ExecutionCarrier(commit.Audit.AggregateId);

            try
            {
                await UpdateExecutionAsync(conn, tx, commit.After.Execution, current.Execution.Version, cancellationToken);
                if (commit.After.BenefitObligation is not null)
                    await WriteObligationAsync(conn, tx, current.BenefitObligation, commit.After.BenefitObligation, cancellationToken);
                ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint.AfterThreadStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, command.CommandName, command.ExecutionId, command.IdempotencyKey,
                    request.Fingerprint, result, cancellationToken);
                ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowExecutionIf(ExecutionWave10PersistenceFaultPoint.BeforeCommitPublish);

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
                return AuthorityResult.Deny(409, "P1_EXECUTION_CONCURRENT_CONFLICT", corr);
            }
            catch (ConflictException)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, "P1_EXECUTION_CONCURRENT_CONFLICT", corr);
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
    private static readonly string ExecutionSelect =
        "SELECT ID, RECOMMENDATION_ID, CANDIDATE_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, CHARTER_APPROVED, PLAN_APPROVED, " +
        "PROGRESS_PERCENT, COMPLETION_DOSSIER_REF, COMPLETION_APPROVED, COMPLETION_SUBMITTED_BY_PERSON_ID, COMPLETION_SUBMITTED_BY_ASSIGNMENT_ID, " +
        "COMPLETION_REVIEWED_BY_PERSON_ID, COMPLETION_REVIEW_DECISION, BENEFIT_ID, " + Ts("CREATED_AT") + ", " + Ts("UPDATED_AT") +
        ", CORRELATION_ID FROM EIMS_EXECUTION";

    private static ExecutionEnvelopeWave10 ReadExecution(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Convert.ToInt64(r.GetValue(4)), r.GetString(5),
        Convert.ToInt64(r.GetValue(6)), Convert.ToInt32(r.GetValue(7)) == 1, Convert.ToInt32(r.GetValue(8)) == 1,
        Convert.ToInt32(r.GetValue(9)), Str(r, 10), Convert.ToInt32(r.GetValue(11)) == 1, Str(r, 12), Str(r, 13), Str(r, 14),
        Str(r, 15), Str(r, 16), ParseTs(r.GetString(17)), ParseTs(r.GetString(18)), r.GetString(19));

    private static async Task<ExecutionThreadEnvelopeWave10?> ReadExecutionThreadAsync(
        OracleConnection conn, OracleTransaction? tx, string executionId, bool forUpdate, CancellationToken ct)
    {
        var execution = await QueryOneAsync(conn, tx, ExecutionSelect + " WHERE ID = :p_id" + (forUpdate ? " FOR UPDATE" : ""),
            executionId, ReadExecution, ct);
        if (execution is null) return null;
        var obligation = await QueryOneAsync(conn, tx,
            "SELECT ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, " + Ts("CREATED_AT") +
            ", CORRELATION_ID FROM EIMS_BENEFIT WHERE EXECUTION_ID = :p_id",
            executionId,
            r => new BenefitObligationEnvelopeWave10(r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToInt64(r.GetValue(3)),
                r.GetString(4), Convert.ToInt64(r.GetValue(5)), ParseTs(r.GetString(6)), r.GetString(7)), ct);
        return new ExecutionThreadEnvelopeWave10(execution, obligation);
    }

    // ---- writes
    private static async Task UpdateExecutionAsync(
        OracleConnection conn, OracleTransaction tx, ExecutionEnvelopeWave10 e, long expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_EXECUTION SET STATE = :p_state, ENTITY_VERSION = :p_ver, CHARTER_APPROVED = :p_ch, PLAN_APPROVED = :p_pl, " +
            "PROGRESS_PERCENT = :p_prog, COMPLETION_DOSSIER_REF = :p_dos, COMPLETION_APPROVED = :p_cap, COMPLETION_SUBMITTED_BY_PERSON_ID = :p_sbp, " +
            "COMPLETION_SUBMITTED_BY_ASSIGNMENT_ID = :p_sba, COMPLETION_REVIEWED_BY_PERSON_ID = :p_rbp, COMPLETION_REVIEW_DECISION = :p_rd, " +
            "BENEFIT_ID = :p_ben, CORRELATION_ID = :p_corr, UPDATED_AT = :p_upd WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_state", OracleDbType.Varchar2, e.State);
        P(cmd, "p_ver", OracleDbType.Int64, e.Version);
        P(cmd, "p_ch", OracleDbType.Int32, e.CharterApproved ? 1 : 0);
        P(cmd, "p_pl", OracleDbType.Int32, e.PlanApproved ? 1 : 0);
        P(cmd, "p_prog", OracleDbType.Int32, e.ProgressPercent);
        P(cmd, "p_dos", OracleDbType.Varchar2, Nz(e.CompletionDossierRef));
        P(cmd, "p_cap", OracleDbType.Int32, e.CompletionApproved ? 1 : 0);
        P(cmd, "p_sbp", OracleDbType.Varchar2, Nz(e.CompletionSubmittedByPersonId));
        P(cmd, "p_sba", OracleDbType.Varchar2, Nz(e.CompletionSubmittedByAssignmentId));
        P(cmd, "p_rbp", OracleDbType.Varchar2, Nz(e.CompletionReviewedByPersonId));
        P(cmd, "p_rd", OracleDbType.Varchar2, Nz(e.CompletionReviewDecision));
        P(cmd, "p_ben", OracleDbType.Varchar2, Nz(e.BenefitId));
        P(cmd, "p_corr", OracleDbType.Varchar2, e.CorrelationId);
        P(cmd, "p_upd", OracleDbType.TimeStampTZ, e.UpdatedAt);
        P(cmd, "p_id", OracleDbType.Varchar2, e.ExecutionId);
        P(cmd, "p_expected", OracleDbType.Int64, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_EXECUTION_CONCURRENT_CONFLICT");
    }

    private static async Task WriteObligationAsync(
        OracleConnection conn, OracleTransaction tx, BenefitObligationEnvelopeWave10? current, BenefitObligationEnvelopeWave10 b, CancellationToken ct)
    {
        if (current is not null && current == b) return;
        await using var cmd = Cmd(conn, tx, current is null
            ? "INSERT INTO EIMS_BENEFIT (ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, CORRELATION_ID, CREATED_AT) " +
              "VALUES (:p_id, :p_exec, :p_idea, :p_iv, :p_state, :p_ver, :p_corr, :p_at)"
            : "UPDATE EIMS_BENEFIT SET STATE = :p_state, ENTITY_VERSION = :p_ver, CORRELATION_ID = :p_corr, UPDATED_AT = SYSTIMESTAMP " +
              "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_id", OracleDbType.Varchar2, b.BenefitId);
        if (current is null)
        {
            P(cmd, "p_exec", OracleDbType.Varchar2, b.ExecutionId);
            P(cmd, "p_idea", OracleDbType.Varchar2, b.IdeaId);
            P(cmd, "p_iv", OracleDbType.Int64, b.ApprovedIdeaVersion);
        }
        P(cmd, "p_state", OracleDbType.Varchar2, b.State);
        P(cmd, "p_ver", OracleDbType.Int64, b.Version);
        P(cmd, "p_corr", OracleDbType.Varchar2, b.CorrelationId);
        if (current is null) P(cmd, "p_at", OracleDbType.TimeStampTZ, b.CreatedAt);
        else P(cmd, "p_expected", OracleDbType.Int64, current.Version);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_EXECUTION_CONCURRENT_CONFLICT");
    }
}
