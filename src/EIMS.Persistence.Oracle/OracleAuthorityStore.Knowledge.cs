using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2f-3b: Wave 13 Knowledge on Oracle (EIMS_KNOWLEDGE_ASSET, one per benefit). One transaction per command:
// knowledge INSERT (create) or UPDATE (ENTITY_VERSION predicate, UPDATED_AT from the envelope) + audit + outbox + idempotency.
// Create idempotency is scoped to the BENEFIT, mutation idempotency to the KNOWLEDGE asset (as in the in-memory store).
public sealed partial class OracleAuthorityStore : IKnowledgeWave13Store
{
    public KnowledgeWave13PersistenceFaultPoint KnowledgeFaultPoint { get; set; }

    private void ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint point)
    {
        if (KnowledgeFaultPoint == point)
            throw new PersistenceAtomicityException("Injected Wave13 Knowledge persistence fault: " + point);
    }

    private static AggregateSnapshot KnowledgeCarrier(string aggregateId) =>
        new(aggregateId, "KNOWLEDGE", "N/A", 0, null, null, null, null, null, null);

    public async ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeAsync(string knowledgeId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await QueryOneAsync(conn, null, KnowledgeSelect + " WHERE ID = :p_id", knowledgeId, ReadKnowledge, cancellationToken);
    }

    public async ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeByBenefitAsync(string benefitId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await QueryOneAsync(conn, null, KnowledgeSelect + " WHERE BENEFIT_ID = :p_id", benefitId, ReadKnowledge, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetKnowledgeIdempotencyAsync(
        string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, commandName, aggregateId, idempotencyKey, cancellationToken);
    }

    public async ValueTask<AuthorityResult> CommitKnowledgeCreateAsync(
        KnowledgeCommandRequestWave13 request, KnowledgeCommitWave13 commit, CancellationToken cancellationToken = default)
    {
        var command = request.Command;
        var corr = command.CorrelationId;
        var benefitId = command.BenefitId ?? string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, command.CommandName, benefitId, command.IdempotencyKey, cancellationToken);
            if (prior is not null)
                return KnowledgeReplayOrConflict(prior, request.Fingerprint, corr);

            if (await QueryOneAsync(conn, tx, KnowledgeSelect + " WHERE BENEFIT_ID = :p_id", benefitId, ReadKnowledge, cancellationToken) is not null)
                return AuthorityResult.Deny(409, "P1_KNOWLEDGE_BENEFIT_ALREADY_HAS_ASSET", corr);
            if (await QueryOneAsync(conn, tx, KnowledgeSelect + " WHERE ID = :p_id", commit.After.KnowledgeId, ReadKnowledge, cancellationToken) is not null)
                return AuthorityResult.Deny(409, "P1_KNOWLEDGE_ID_CONFLICT", corr);

            KnowledgeTransactionalStoreWave13.ValidateCreate(request, commit);

            var events = commit.OutboxEvents.ToArray();
            var result = KnowledgeResult(commit, events, corr);
            var carrier = KnowledgeCarrier(commit.Audit.AggregateId);
            try
            {
                await InsertKnowledgeAsync(conn, tx, commit.After, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterStateStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, command.CommandName, benefitId, command.IdempotencyKey,
                    request.Fingerprint, result, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue;
                if (Has(ex, "UQ_KNOWLEDGE_BENEFIT"))
                    return AuthorityResult.Deny(409, "P1_KNOWLEDGE_BENEFIT_ALREADY_HAS_ASSET", corr);
                if (Has(ex, "PK_KNOWLEDGE_ASSET"))
                    return AuthorityResult.Deny(409, "P1_KNOWLEDGE_ID_CONFLICT", corr);
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

    public async ValueTask<AuthorityResult> CommitKnowledgeCommandAsync(
        KnowledgeCommandRequestWave13 request, KnowledgeCommitWave13 commit, CancellationToken cancellationToken = default)
    {
        var command = request.Command;
        var corr = command.CorrelationId;
        var knowledgeId = command.KnowledgeId ?? string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, command.CommandName, knowledgeId, command.IdempotencyKey, cancellationToken);
            if (prior is not null)
                return KnowledgeReplayOrConflict(prior, request.Fingerprint, corr);

            var current = await QueryOneAsync(conn, tx, KnowledgeSelect + " WHERE ID = :p_id FOR UPDATE", knowledgeId, ReadKnowledge, cancellationToken);
            if (request.Before is null || current is null
                || current.Version != request.Before.Version || current.Version != command.ExpectedVersion)
                return AuthorityResult.Deny(409, "P1_KNOWLEDGE_CONCURRENT_CONFLICT", corr);

            KnowledgeTransactionalStoreWave13.ValidateMutation(request, commit);

            var events = commit.OutboxEvents.ToArray();
            var result = KnowledgeResult(commit, events, corr);
            var carrier = KnowledgeCarrier(commit.Audit.AggregateId);
            try
            {
                await UpdateKnowledgeAsync(conn, tx, commit.After, current.Version, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterStateStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, command.CommandName, knowledgeId, command.IdempotencyKey,
                    request.Fingerprint, result, cancellationToken);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowKnowledgeIf(KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish);

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
                throw;
            }
            catch (ConflictException)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, "P1_KNOWLEDGE_CONCURRENT_CONFLICT", corr);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    private static AuthorityResult KnowledgeReplayOrConflict(IdempotencyRecord prior, string fingerprint, string correlationId)
    {
        if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
            return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", correlationId);
        return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = correlationId };
    }

    private static AuthorityResult KnowledgeResult(KnowledgeCommitWave13 commit, OutboxEnvelope[] events, string correlationId) =>
        new(200, "P1_KNOWLEDGE_COMMAND_COMMITTED", Allowed: true, StateMutated: true, IdempotentReplay: false,
            NewVersion: commit.After.Version, CorrelationId: correlationId, EmittedEvents: events.Select(x => x.EventName).ToArray());

    // ---- reads / writes
    private static readonly string KnowledgeSelect =
        "SELECT ID, BENEFIT_ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, AUTHOR_PERSON_ID, AUTHOR_ASSIGNMENT_ID, " +
        "AUTHOR_POLICY, SCOPE_CODE, VALIDATED_BY_PERSON_ID, VALIDATED_BY_ASSIGNMENT_ID, PUBLISHED_BY_PERSON_ID, PUBLISHED_BY_ASSIGNMENT_ID, " +
        "PUBLICATION_DOSSIER_REF, " + Ts("CREATED_AT") + ", " + Ts("UPDATED_AT") + ", CORRELATION_ID FROM EIMS_KNOWLEDGE_ASSET";

    private static KnowledgeEnvelopeWave13 ReadKnowledge(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Convert.ToInt64(r.GetValue(4)), r.GetString(5),
        Convert.ToInt64(r.GetValue(6)), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10),
        Str(r, 11), Str(r, 12), Str(r, 13), Str(r, 14), Str(r, 15), ParseTs(r.GetString(16)), ParseTs(r.GetString(17)), r.GetString(18));

    private static async Task InsertKnowledgeAsync(OracleConnection conn, OracleTransaction tx, KnowledgeEnvelopeWave13 k, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_KNOWLEDGE_ASSET (ID, BENEFIT_ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, AUTHOR_PERSON_ID, " +
            "AUTHOR_ASSIGNMENT_ID, AUTHOR_POLICY, SCOPE_CODE, VALIDATED_BY_PERSON_ID, VALIDATED_BY_ASSIGNMENT_ID, PUBLISHED_BY_PERSON_ID, " +
            "PUBLISHED_BY_ASSIGNMENT_ID, PUBLICATION_DOSSIER_REF, CORRELATION_ID, CREATED_AT, UPDATED_AT) " +
            "VALUES (:p_id, :p_ben, :p_exec, :p_idea, :p_iv, :p_state, :p_ver, :p_ap, :p_aa, :p_pol, :p_scope, :p_vp, :p_va, :p_pp, :p_pa, :p_dos, :p_corr, :p_at, :p_upd)");
        P(cmd, "p_id", OracleDbType.Varchar2, k.KnowledgeId);
        P(cmd, "p_ben", OracleDbType.Varchar2, k.BenefitId);
        P(cmd, "p_exec", OracleDbType.Varchar2, k.ExecutionId);
        P(cmd, "p_idea", OracleDbType.Varchar2, k.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, k.ApprovedIdeaVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, k.State);
        P(cmd, "p_ver", OracleDbType.Int64, k.Version);
        P(cmd, "p_ap", OracleDbType.Varchar2, k.AuthorPersonId);
        P(cmd, "p_aa", OracleDbType.Varchar2, k.AuthorAssignmentId);
        P(cmd, "p_pol", OracleDbType.Varchar2, k.AuthorPolicy);
        P(cmd, "p_scope", OracleDbType.Varchar2, k.Scope);
        BindKnowledgeMutable(cmd, k);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, k.CreatedAt);
        P(cmd, "p_upd", OracleDbType.TimeStampTZ, k.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateKnowledgeAsync(
        OracleConnection conn, OracleTransaction tx, KnowledgeEnvelopeWave13 k, long expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_KNOWLEDGE_ASSET SET STATE = :p_state, ENTITY_VERSION = :p_ver, VALIDATED_BY_PERSON_ID = :p_vp, VALIDATED_BY_ASSIGNMENT_ID = :p_va, " +
            "PUBLISHED_BY_PERSON_ID = :p_pp, PUBLISHED_BY_ASSIGNMENT_ID = :p_pa, PUBLICATION_DOSSIER_REF = :p_dos, CORRELATION_ID = :p_corr, UPDATED_AT = :p_upd " +
            "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_state", OracleDbType.Varchar2, k.State);
        P(cmd, "p_ver", OracleDbType.Int64, k.Version);
        BindKnowledgeMutable(cmd, k);
        P(cmd, "p_upd", OracleDbType.TimeStampTZ, k.UpdatedAt);
        P(cmd, "p_id", OracleDbType.Varchar2, k.KnowledgeId);
        P(cmd, "p_expected", OracleDbType.Int64, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_KNOWLEDGE_CONCURRENT_CONFLICT");
    }

    // Binds the 5 mutable text columns + correlation id (shared by INSERT and UPDATE).
    private static void BindKnowledgeMutable(OracleCommand cmd, KnowledgeEnvelopeWave13 k)
    {
        P(cmd, "p_vp", OracleDbType.Varchar2, Nz(k.ValidatedByPersonId));
        P(cmd, "p_va", OracleDbType.Varchar2, Nz(k.ValidatedByAssignmentId));
        P(cmd, "p_pp", OracleDbType.Varchar2, Nz(k.PublishedByPersonId));
        P(cmd, "p_pa", OracleDbType.Varchar2, Nz(k.PublishedByAssignmentId));
        P(cmd, "p_dos", OracleDbType.Varchar2, Nz(k.PublicationDossierRef));
        P(cmd, "p_corr", OracleDbType.Varchar2, k.CorrelationId);
    }
}
