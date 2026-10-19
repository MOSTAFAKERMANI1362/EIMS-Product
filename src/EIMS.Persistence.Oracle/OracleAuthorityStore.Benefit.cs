using System.Data.Common;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S2f-3a: Wave 11 Benefit on the shared EIMS_BENEFIT row (obligation inserted by the Execution completion, S2f-2).
// Commands: one transaction = benefit UPDATE (ENTITY_VERSION predicate, UPDATED_AT from the envelope) + audit + outbox + idempotency,
// row locked FOR UPDATE. The system intake (MaterializeBenefitObligationAsync) is idempotent: with the shared row it normally
// returns ALREADY_MATERIALIZED, and materializes the row itself only if it is absent.
public sealed partial class OracleAuthorityStore : IBenefitWave11Store
{
    private const string BenefitIntakeCommand = "system.benefit-obligation-intake";

    public BenefitWave11PersistenceFaultPoint BenefitFaultPoint { get; set; }

    private void ThrowBenefitIf(BenefitWave11PersistenceFaultPoint point)
    {
        if (BenefitFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave11 Benefit persistence fault at {point}.");
    }

    private static AggregateSnapshot BenefitCarrier(string aggregateId) =>
        new(aggregateId, "BENEFIT", "N/A", 0, null, null, null, null, null, null);

    public async ValueTask<BenefitEnvelopeWave11?> GetBenefitAsync(string benefitId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadBenefitAsync(conn, null, benefitId, false, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetBenefitIdempotencyAsync(
        string commandName, string benefitId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, commandName, benefitId, idempotencyKey, cancellationToken);
    }

    public async ValueTask<AuthorityResult> MaterializeBenefitObligationAsync(
        BenefitObligationSourceEvidenceWave11 source, string fingerprint, CancellationToken cancellationToken = default)
    {
        var corr = source.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, BenefitIntakeCommand, source.BenefitId, source.BenefitId, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P1_BENEFIT_INTAKE_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var existing = await ReadBenefitAsync(conn, tx, source.BenefitId, true, cancellationToken);
            try
            {
                if (existing is not null)
                {
                    if (!SameBenefitSource(existing, source))
                        return AuthorityResult.Deny(409, "P1_BENEFIT_INTAKE_THREAD_CONFLICT", corr);
                    var already = new AuthorityResult(
                        200, "P1_BENEFIT_INTAKE_ALREADY_MATERIALIZED", Allowed: true, StateMutated: false, IdempotentReplay: true,
                        NewVersion: existing.Version, CorrelationId: corr, EmittedEvents: Array.Empty<string>());
                    await InsertIdempotencyRawAsync(conn, tx, BenefitIntakeCommand, source.BenefitId, source.BenefitId, fingerprint, already, cancellationToken);
                    tx.Commit();
                    return already;
                }

                var benefit = new BenefitEnvelopeWave11(
                    source.BenefitId, source.ExecutionId, source.IdeaId, source.ApprovedIdeaVersion, "OBLIGATION_PENDING_ACCEPTANCE", 1,
                    null, null, null, null, null, null, null, null, null, null, null, null,
                    source.OccurredAt, source.OccurredAt, corr);
                var audit = new AuditEnvelope(
                    "AUD-" + Guid.NewGuid().ToString("N"), "SYSTEM_SERVICE", "SYSTEM_EVENT", "SYSTEM_EVENT", new[] { "SYSTEM_SERVICE" }, "SYSTEM",
                    benefit.BenefitId, benefit.Version, "P1-BENEFIT-WAVE11-INTAKE|source=" + source.EvidenceRef, source.OccurredAt, corr, BenefitIntakeCommand);

                await InsertBenefitAsync(conn, tx, benefit, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterStateStaged);
                await InsertAuditAsync(conn, tx, BenefitCarrier(benefit.BenefitId), audit, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterAuditStaged);

                var result = new AuthorityResult(
                    200, "P1_BENEFIT_INTAKE_MATERIALIZED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                    NewVersion: 1, CorrelationId: corr, EmittedEvents: Array.Empty<string>());
                await InsertIdempotencyRawAsync(conn, tx, BenefitIntakeCommand, source.BenefitId, source.BenefitId, fingerprint, result, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1 || ex.Number == 2291)
            {
                tx.Rollback();
                if (ex.Number == 1 && Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue;
                return AuthorityResult.Deny(409, "P1_BENEFIT_INTAKE_THREAD_CONFLICT", corr);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    public async ValueTask<AuthorityResult> CommitBenefitCommandAsync(
        BenefitCommandRequestWave11 request, BenefitCommitWave11 commit, CancellationToken cancellationToken = default)
    {
        var command = request.Command;
        var corr = command.CorrelationId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();

            var prior = await ReadIdempotencyAsync(conn, tx, command.CommandName, command.BenefitId, command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", corr);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var current = await ReadBenefitAsync(conn, tx, command.BenefitId, true, cancellationToken);
            if (current is null || current.Version != request.Before.Version || current.Version != command.ExpectedVersion)
                return AuthorityResult.Deny(409, "P1_BENEFIT_CONCURRENT_CONFLICT", corr);

            BenefitTransactionalStoreWave11.ValidateCommit(request, commit);

            var events = commit.OutboxEvents.ToArray();
            var result = new AuthorityResult(
                200, "P1_BENEFIT_COMMAND_COMMITTED", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.After.Version, CorrelationId: corr, EmittedEvents: events.Select(x => x.EventName).ToArray());
            var carrier = BenefitCarrier(commit.Audit.AggregateId);

            try
            {
                await UpdateBenefitAsync(conn, tx, commit.After, current.Version, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterStateStaged);

                await InsertAuditAsync(conn, tx, carrier, commit.Audit, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterAuditStaged);

                foreach (var e in events)
                    await InsertOutboxAsync(conn, tx, carrier, e, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyRawAsync(conn, tx, command.CommandName, command.BenefitId, command.IdempotencyKey,
                    request.Fingerprint, result, cancellationToken);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowBenefitIf(BenefitWave11PersistenceFaultPoint.BeforeCommitPublish);

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
                return AuthorityResult.Deny(409, "P1_BENEFIT_CONCURRENT_CONFLICT", corr);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    private static bool SameBenefitSource(BenefitEnvelopeWave11 existing, BenefitObligationSourceEvidenceWave11 source) =>
        string.Equals(existing.BenefitId, source.BenefitId, StringComparison.Ordinal)
        && string.Equals(existing.ExecutionId, source.ExecutionId, StringComparison.Ordinal)
        && string.Equals(existing.IdeaId, source.IdeaId, StringComparison.Ordinal)
        && existing.ApprovedIdeaVersion == source.ApprovedIdeaVersion
        && existing.Version == 1
        && string.Equals(existing.State, "OBLIGATION_PENDING_ACCEPTANCE", StringComparison.Ordinal);

    // ---- reads / writes
    private static readonly string BenefitSelect =
        "SELECT ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, BASELINE_EVIDENCE_REF, TARGET_EVIDENCE_REF, " +
        "MEASUREMENT_PLAN_REF, MEASUREMENT_DOSSIER_REF, VERIFICATION_DOSSIER_REF, ATTRIBUTION_DOSSIER_REF, REALIZATION_DOSSIER_REF, " +
        "ACCEPTED_BY_PERSON_ID, ACCEPTED_BY_ASSIGNMENT_ID, MEASURED_BY_PERSON_ID, VERIFIED_BY_PERSON_ID, KNOWLEDGE_ID, " + Ts("CREATED_AT") +
        ", " + Ts("UPDATED_AT") + ", CORRELATION_ID FROM EIMS_BENEFIT";

    private static BenefitEnvelopeWave11 ReadBenefit(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToInt64(r.GetValue(3)), r.GetString(4), Convert.ToInt64(r.GetValue(5)),
        Str(r, 6), Str(r, 7), Str(r, 8), Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12), Str(r, 13), Str(r, 14), Str(r, 15), Str(r, 16), Str(r, 17),
        ParseTs(r.GetString(18)), ParseTs(r.GetString(19)), r.GetString(20));

    private static Task<BenefitEnvelopeWave11?> ReadBenefitAsync(
        OracleConnection conn, OracleTransaction? tx, string benefitId, bool forUpdate, CancellationToken ct) =>
        QueryOneAsync(conn, tx, BenefitSelect + " WHERE ID = :p_id" + (forUpdate ? " FOR UPDATE" : ""), benefitId, ReadBenefit, ct);

    private static async Task InsertBenefitAsync(OracleConnection conn, OracleTransaction tx, BenefitEnvelopeWave11 b, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_BENEFIT (ID, EXECUTION_ID, IDEA_ID, APPROVED_IDEA_VERSION, STATE, ENTITY_VERSION, BASELINE_EVIDENCE_REF, TARGET_EVIDENCE_REF, " +
            "MEASUREMENT_PLAN_REF, MEASUREMENT_DOSSIER_REF, VERIFICATION_DOSSIER_REF, ATTRIBUTION_DOSSIER_REF, REALIZATION_DOSSIER_REF, ACCEPTED_BY_PERSON_ID, " +
            "ACCEPTED_BY_ASSIGNMENT_ID, MEASURED_BY_PERSON_ID, VERIFIED_BY_PERSON_ID, KNOWLEDGE_ID, CORRELATION_ID, CREATED_AT, UPDATED_AT) " +
            "VALUES (:p_id, :p_exec, :p_idea, :p_iv, :p_state, :p_ver, :p_base, :p_target, :p_plan, :p_mdos, :p_vdos, :p_ados, :p_rdos, :p_accp, :p_acca, " +
            ":p_meas, :p_ver2, :p_know, :p_corr, :p_at, :p_upd)");
        P(cmd, "p_id", OracleDbType.Varchar2, b.BenefitId);
        P(cmd, "p_exec", OracleDbType.Varchar2, b.ExecutionId);
        P(cmd, "p_idea", OracleDbType.Varchar2, b.IdeaId);
        P(cmd, "p_iv", OracleDbType.Int64, b.ApprovedIdeaVersion);
        P(cmd, "p_state", OracleDbType.Varchar2, b.State);
        P(cmd, "p_ver", OracleDbType.Int64, b.Version);
        BindBenefitFields(cmd, b);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, b.CreatedAt);
        P(cmd, "p_upd", OracleDbType.TimeStampTZ, b.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateBenefitAsync(
        OracleConnection conn, OracleTransaction tx, BenefitEnvelopeWave11 b, long expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_BENEFIT SET STATE = :p_state, ENTITY_VERSION = :p_ver, BASELINE_EVIDENCE_REF = :p_base, TARGET_EVIDENCE_REF = :p_target, " +
            "MEASUREMENT_PLAN_REF = :p_plan, MEASUREMENT_DOSSIER_REF = :p_mdos, VERIFICATION_DOSSIER_REF = :p_vdos, ATTRIBUTION_DOSSIER_REF = :p_ados, " +
            "REALIZATION_DOSSIER_REF = :p_rdos, ACCEPTED_BY_PERSON_ID = :p_accp, ACCEPTED_BY_ASSIGNMENT_ID = :p_acca, MEASURED_BY_PERSON_ID = :p_meas, " +
            "VERIFIED_BY_PERSON_ID = :p_ver2, KNOWLEDGE_ID = :p_know, CORRELATION_ID = :p_corr, UPDATED_AT = :p_upd " +
            "WHERE ID = :p_id AND ENTITY_VERSION = :p_expected");
        P(cmd, "p_state", OracleDbType.Varchar2, b.State);
        P(cmd, "p_ver", OracleDbType.Int64, b.Version);
        BindBenefitFields(cmd, b);
        P(cmd, "p_upd", OracleDbType.TimeStampTZ, b.UpdatedAt);
        P(cmd, "p_id", OracleDbType.Varchar2, b.BenefitId);
        P(cmd, "p_expected", OracleDbType.Int64, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("P1_BENEFIT_CONCURRENT_CONFLICT");
    }

    // Binds the 12 mutable text columns + correlation id (shared by INSERT and UPDATE; parameter names match both statements).
    private static void BindBenefitFields(OracleCommand cmd, BenefitEnvelopeWave11 b)
    {
        P(cmd, "p_base", OracleDbType.Varchar2, Nz(b.BaselineEvidenceRef));
        P(cmd, "p_target", OracleDbType.Varchar2, Nz(b.TargetEvidenceRef));
        P(cmd, "p_plan", OracleDbType.Varchar2, Nz(b.MeasurementPlanRef));
        P(cmd, "p_mdos", OracleDbType.Varchar2, Nz(b.MeasurementDossierRef));
        P(cmd, "p_vdos", OracleDbType.Varchar2, Nz(b.VerificationDossierRef));
        P(cmd, "p_ados", OracleDbType.Varchar2, Nz(b.AttributionDossierRef));
        P(cmd, "p_rdos", OracleDbType.Varchar2, Nz(b.RealizationDossierRef));
        P(cmd, "p_accp", OracleDbType.Varchar2, Nz(b.AcceptedByPersonId));
        P(cmd, "p_acca", OracleDbType.Varchar2, Nz(b.AcceptedByAssignmentId));
        P(cmd, "p_meas", OracleDbType.Varchar2, Nz(b.MeasuredByPersonId));
        P(cmd, "p_ver2", OracleDbType.Varchar2, Nz(b.VerifiedByPersonId));
        P(cmd, "p_know", OracleDbType.Varchar2, Nz(b.KnowledgeId));
        P(cmd, "p_corr", OracleDbType.Varchar2, b.CorrelationId);
    }
}
