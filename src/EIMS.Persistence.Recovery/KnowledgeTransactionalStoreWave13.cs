using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum KnowledgeWave13PersistenceFaultPoint
{
    None = 0,
    AfterStateStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5
}

/// <summary>
/// Provider-neutral Wave13 reference persistence. It proves atomic Knowledge semantics only.
/// It is not an Oracle adapter and is not physical OP-04 evidence.
/// </summary>
public sealed class KnowledgeTransactionalStoreWave13 : IKnowledgeWave13Store
{
    private readonly object _sync = new();
    private readonly Dictionary<string, KnowledgeEnvelopeWave13> _knowledge = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knowledgeByBenefit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();

    public KnowledgeTransactionalStoreWave13(params KnowledgeEnvelopeWave13[] initial)
    {
        foreach (var item in initial)
        {
            _knowledge.Add(item.KnowledgeId, item);
            _knowledgeByBenefit.Add(item.BenefitId, item.KnowledgeId);
        }
    }

    public PersistenceContractDescriptor Contract { get; } = PersistenceContractDescriptor.RecoveryBaseline();
    public KnowledgeWave13PersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<KnowledgeEnvelopeWave13> KnowledgeAssets
    {
        get { lock (_sync) return Array.AsReadOnly(_knowledge.Values.ToArray()); }
    }

    public IReadOnlyCollection<AuditEnvelope> KnowledgeAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> KnowledgeOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> KnowledgeIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeAsync(string knowledgeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_knowledge.TryGetValue(knowledgeId, out var value) ? value : null);
    }

    public ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeByBenefitAsync(string benefitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_knowledgeByBenefit.TryGetValue(benefitId, out var knowledgeId))
                return ValueTask.FromResult<KnowledgeEnvelopeWave13?>(null);
            return ValueTask.FromResult(_knowledge.TryGetValue(knowledgeId, out var value) ? value : null);
        }
    }

    public ValueTask<IdempotencyRecord?> GetKnowledgeIdempotencyAsync(
        string commandName,
        string aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(Key(commandName, aggregateId, idempotencyKey), out var value)
                ? value : null);
    }

    public ValueTask<AuthorityResult> CommitKnowledgeCreateAsync(
        KnowledgeCommandRequestWave13 request,
        KnowledgeCommitWave13 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var command = request.Command;
            var benefitId = command.BenefitId ?? string.Empty;
            var idemKey = Key(command.CommandName, benefitId, command.IdempotencyKey);
            if (_idempotency.TryGetValue(idemKey, out var prior))
                return ValueTask.FromResult(ReplayOrConflict(prior, request.Fingerprint, command.CorrelationId));

            if (_knowledgeByBenefit.ContainsKey(benefitId))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_KNOWLEDGE_BENEFIT_ALREADY_HAS_ASSET", command.CorrelationId));
            if (_knowledge.ContainsKey(commit.After.KnowledgeId))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_KNOWLEDGE_ID_CONFLICT", command.CorrelationId));

            ValidateCreate(request, commit);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterStateStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterOutboxStaged);

            var result = Result(command, commit);
            var idem = new IdempotencyRecord(command.CommandName, benefitId, command.IdempotencyKey, request.Fingerprint, result);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish);

            _knowledge[commit.After.KnowledgeId] = commit.After;
            _knowledgeByBenefit[benefitId] = commit.After.KnowledgeId;
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency[idemKey] = idem;
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<AuthorityResult> CommitKnowledgeCommandAsync(
        KnowledgeCommandRequestWave13 request,
        KnowledgeCommitWave13 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var command = request.Command;
            var knowledgeId = command.KnowledgeId ?? string.Empty;
            var idemKey = Key(command.CommandName, knowledgeId, command.IdempotencyKey);
            if (_idempotency.TryGetValue(idemKey, out var prior))
                return ValueTask.FromResult(ReplayOrConflict(prior, request.Fingerprint, command.CorrelationId));

            if (request.Before is null
                || !_knowledge.TryGetValue(knowledgeId, out var current)
                || current.Version != request.Before.Version
                || current.Version != command.ExpectedVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_KNOWLEDGE_CONCURRENT_CONFLICT", command.CorrelationId));

            ValidateMutation(request, commit);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterStateStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterOutboxStaged);

            var result = Result(command, commit);
            var idem = new IdempotencyRecord(command.CommandName, knowledgeId, command.IdempotencyKey, request.Fingerprint, result);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(KnowledgeWave13PersistenceFaultPoint.BeforeCommitPublish);

            _knowledge[knowledgeId] = commit.After;
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency[idemKey] = idem;
            return ValueTask.FromResult(result);
        }
    }

    private static AuthorityResult ReplayOrConflict(IdempotencyRecord prior, string fingerprint, string correlationId)
    {
        if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
            return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", correlationId);
        return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = correlationId };
    }

    private static AuthorityResult Result(KnowledgeCommandWave13 command, KnowledgeCommitWave13 commit) =>
        new(
            200,
            "P1_KNOWLEDGE_COMMAND_COMMITTED",
            true,
            true,
            false,
            commit.After.Version,
            command.CorrelationId,
            commit.OutboxEvents.Select(x => x.EventName).ToArray());

    private static void ValidateCreate(KnowledgeCommandRequestWave13 request, KnowledgeCommitWave13 commit)
    {
        var after = commit.After;
        if (after.Version != 1
            || !string.Equals(after.State, "DRAFT", StringComparison.Ordinal)
            || !string.Equals(after.BenefitId, request.Command.BenefitId, StringComparison.Ordinal))
            throw new InvalidOperationException("P1_KNOWLEDGE_CREATE_INVALID");
        ValidateEvidence(request, commit);
    }

    private static void ValidateMutation(KnowledgeCommandRequestWave13 request, KnowledgeCommitWave13 commit)
    {
        var before = request.Before!;
        var after = commit.After;
        if (after.Version != before.Version + 1)
            throw new InvalidOperationException("P1_KNOWLEDGE_VERSION_ADVANCE_INVALID");
        if (!string.Equals(after.KnowledgeId, before.KnowledgeId, StringComparison.Ordinal)
            || !string.Equals(after.BenefitId, before.BenefitId, StringComparison.Ordinal)
            || !string.Equals(after.ExecutionId, before.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(after.IdeaId, before.IdeaId, StringComparison.Ordinal)
            || after.ApprovedIdeaVersion != before.ApprovedIdeaVersion
            || !string.Equals(after.AuthorPersonId, before.AuthorPersonId, StringComparison.Ordinal)
            || !string.Equals(after.AuthorAssignmentId, before.AuthorAssignmentId, StringComparison.Ordinal)
            || !string.Equals(after.AuthorPolicy, before.AuthorPolicy, StringComparison.Ordinal)
            || !string.Equals(after.Scope, before.Scope, StringComparison.Ordinal))
            throw new InvalidOperationException("P1_KNOWLEDGE_DIGITAL_THREAD_BROKEN");
        ValidateEvidence(request, commit);
    }

    private static void ValidateEvidence(KnowledgeCommandRequestWave13 request, KnowledgeCommitWave13 commit)
    {
        if (!string.Equals(commit.Audit.CommandName, request.Command.CommandName, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.PersonId, request.Actor.PersonId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.Assignment, request.Actor.AssignmentId, StringComparison.Ordinal)
            || commit.OutboxEvents.Count != 1
            || commit.OutboxEvents.Any(x => !string.Equals(x.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
                || !string.Equals(x.AggregateId, commit.After.KnowledgeId, StringComparison.Ordinal)
                || x.AggregateVersion != commit.After.Version))
            throw new InvalidOperationException("P1_KNOWLEDGE_AUDIT_OUTBOX_INVALID");
    }

    private void ThrowIf(KnowledgeWave13PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException("Injected Wave13 Knowledge persistence fault: " + point);
    }

    private static string Key(string commandName, string aggregateId, string idempotencyKey) =>
        commandName + "\u001f" + aggregateId + "\u001f" + idempotencyKey;
}
