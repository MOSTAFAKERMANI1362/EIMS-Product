using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum BenefitWave11PersistenceFaultPoint
{
    None = 0,
    AfterStateStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5
}

/// <summary>
/// Provider-neutral Wave11 reference persistence. It proves atomic Benefit semantics only.
/// It is not an Oracle adapter and is not physical OP-04 evidence.
/// </summary>
public sealed class BenefitTransactionalStoreWave11 : IBenefitWave11Store
{
    private const string IntakeCommand = "system.benefit-obligation-intake";
    private readonly object _sync = new();
    private readonly Dictionary<string, BenefitEnvelopeWave11> _benefits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();

    public BenefitTransactionalStoreWave11(params BenefitEnvelopeWave11[] initialBenefits)
    {
        foreach (var benefit in initialBenefits)
            _benefits.Add(benefit.BenefitId, benefit);
    }

    public PersistenceContractDescriptor Contract { get; } = PersistenceContractDescriptor.RecoveryBaseline();
    public BenefitWave11PersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<BenefitEnvelopeWave11> Benefits
    {
        get { lock (_sync) return Array.AsReadOnly(_benefits.Values.ToArray()); }
    }

    public IReadOnlyCollection<AuditEnvelope> BenefitAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> BenefitOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> BenefitIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<BenefitEnvelopeWave11?> GetBenefitAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_benefits.TryGetValue(benefitId, out var value) ? value : null);
    }

    public ValueTask<IdempotencyRecord?> GetBenefitIdempotencyAsync(
        string commandName,
        string benefitId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(Key(commandName, benefitId, idempotencyKey), out var value)
                ? value : null);
    }

    public ValueTask<AuthorityResult> MaterializeBenefitObligationAsync(
        BenefitObligationSourceEvidenceWave11 source,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var idemKey = Key(IntakeCommand, source.BenefitId, source.BenefitId);
            if (_idempotency.TryGetValue(idemKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_BENEFIT_INTAKE_CONFLICT", source.CorrelationId));
                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = source.CorrelationId
                });
            }

            if (_benefits.TryGetValue(source.BenefitId, out var existing))
            {
                if (!SameSource(existing, source))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_BENEFIT_INTAKE_THREAD_CONFLICT", source.CorrelationId));

                var replay = new AuthorityResult(
                    200,
                    "P1_BENEFIT_INTAKE_ALREADY_MATERIALIZED",
                    true,
                    false,
                    true,
                    existing.Version,
                    source.CorrelationId,
                    Array.Empty<string>());
                _idempotency[idemKey] = new IdempotencyRecord(IntakeCommand, source.BenefitId, source.BenefitId, fingerprint, replay);
                return ValueTask.FromResult(replay);
            }

            var benefit = new BenefitEnvelopeWave11(
                source.BenefitId,
                source.ExecutionId,
                source.IdeaId,
                source.ApprovedIdeaVersion,
                "OBLIGATION_PENDING_ACCEPTANCE",
                1,
                null, null, null, null, null, null, null,
                null, null, null, null, null,
                source.OccurredAt,
                source.OccurredAt,
                source.CorrelationId);

            var audit = new AuditEnvelope(
                "AUD-" + Guid.NewGuid().ToString("N"),
                "SYSTEM_SERVICE",
                "SYSTEM_EVENT",
                "SYSTEM_EVENT",
                new[] { "SYSTEM_SERVICE" },
                "SYSTEM",
                benefit.BenefitId,
                benefit.Version,
                "P1-BENEFIT-WAVE11-INTAKE|source=" + source.EvidenceRef,
                source.OccurredAt,
                source.CorrelationId,
                IntakeCommand);

            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterStateStaged);
            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterAuditStaged);

            var result = new AuthorityResult(
                200,
                "P1_BENEFIT_INTAKE_MATERIALIZED",
                true,
                true,
                false,
                1,
                source.CorrelationId,
                Array.Empty<string>());
            var idem = new IdempotencyRecord(
                IntakeCommand,
                source.BenefitId,
                source.BenefitId,
                fingerprint,
                result);

            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(BenefitWave11PersistenceFaultPoint.BeforeCommitPublish);

            _benefits[source.BenefitId] = benefit;
            _audits.Add(audit);
            _idempotency[idemKey] = idem;
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<AuthorityResult> CommitBenefitCommandAsync(
        BenefitCommandRequestWave11 request,
        BenefitCommitWave11 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var command = request.Command;
            var idemKey = Key(command.CommandName, command.BenefitId, command.IdempotencyKey);
            if (_idempotency.TryGetValue(idemKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId));
                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = command.CorrelationId
                });
            }

            if (!_benefits.TryGetValue(command.BenefitId, out var current)
                || current.Version != request.Before.Version
                || current.Version != command.ExpectedVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_BENEFIT_CONCURRENT_CONFLICT", command.CorrelationId));

            ValidateCommit(request, commit);
            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterStateStaged);
            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterOutboxStaged);

            var emitted = commit.OutboxEvents.Select(x => x.EventName).ToArray();
            var result = new AuthorityResult(
                200,
                "P1_BENEFIT_COMMAND_COMMITTED",
                true,
                true,
                false,
                commit.After.Version,
                command.CorrelationId,
                emitted);
            var idem = new IdempotencyRecord(
                command.CommandName,
                command.BenefitId,
                command.IdempotencyKey,
                request.Fingerprint,
                result);

            ThrowIf(BenefitWave11PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(BenefitWave11PersistenceFaultPoint.BeforeCommitPublish);

            _benefits[command.BenefitId] = commit.After;
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency[idemKey] = idem;
            return ValueTask.FromResult(result);
        }
    }

    private static void ValidateCommit(BenefitCommandRequestWave11 request, BenefitCommitWave11 commit)
    {
        var before = request.Before;
        var after = commit.After;
        if (after.Version != before.Version + 1)
            throw new InvalidOperationException("P1_BENEFIT_VERSION_ADVANCE_INVALID");

        if (!string.Equals(after.BenefitId, before.BenefitId, StringComparison.Ordinal)
            || !string.Equals(after.ExecutionId, before.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(after.IdeaId, before.IdeaId, StringComparison.Ordinal)
            || after.ApprovedIdeaVersion != before.ApprovedIdeaVersion)
            throw new InvalidOperationException("P1_BENEFIT_DIGITAL_THREAD_BROKEN");

        if (!string.Equals(commit.Audit.CommandName, request.Command.CommandName, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.PersonId, request.Actor.PersonId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.Assignment, request.Actor.AssignmentId, StringComparison.Ordinal)
            || commit.OutboxEvents.Count != 1
            || commit.OutboxEvents.Any(x => !string.Equals(x.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
                || !string.Equals(x.AggregateId, after.BenefitId, StringComparison.Ordinal)
                || x.AggregateVersion != after.Version))
            throw new InvalidOperationException("P1_BENEFIT_AUDIT_OUTBOX_INVALID");
    }

    private static bool SameSource(BenefitEnvelopeWave11 existing, BenefitObligationSourceEvidenceWave11 source) =>
        string.Equals(existing.BenefitId, source.BenefitId, StringComparison.Ordinal)
        && string.Equals(existing.ExecutionId, source.ExecutionId, StringComparison.Ordinal)
        && string.Equals(existing.IdeaId, source.IdeaId, StringComparison.Ordinal)
        && existing.ApprovedIdeaVersion == source.ApprovedIdeaVersion
        && existing.Version == 1
        && string.Equals(existing.State, "OBLIGATION_PENDING_ACCEPTANCE", StringComparison.Ordinal);

    private void ThrowIf(BenefitWave11PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave11 Benefit persistence fault at {point}.");
    }

    private static string Key(string command, string benefitId, string key) =>
        $"{command}\u001f{benefitId}\u001f{key}";
}
