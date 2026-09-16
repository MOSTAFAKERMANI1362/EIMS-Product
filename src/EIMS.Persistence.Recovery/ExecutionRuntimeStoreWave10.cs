using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum ExecutionIntakeWave10PersistenceFaultPoint
{
    None = 0,
    AfterThreadStaged = 1,
    AfterAuditStaged = 2,
    AfterIdempotencyStaged = 3,
    BeforeCommitPublish = 4
}

/// <summary>
/// Wave 10 runtime store that composes the proven per-Execution transactional store with
/// system-only materialization from the authoritative Wave 9 handoff event.
/// No new domain outbox event is invented during intake because the source event itself is authoritative.
/// This remains provider-neutral reference persistence, not the physical Oracle adapter.
/// </summary>
public sealed class ExecutionRuntimeStoreWave10 : IExecutionWave10Store, IExecutionHandoffIntakeStoreWave10
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExecutionTransactionalStoreWave10> _executionStores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _intakeIdempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _intakeAudits = new();

    public ExecutionRuntimeStoreWave10(params ExecutionThreadEnvelopeWave10[] initialThreads)
    {
        foreach (var thread in initialThreads)
            _executionStores.Add(thread.Execution.ExecutionId, new ExecutionTransactionalStoreWave10(thread));
    }

    public ExecutionIntakeWave10PersistenceFaultPoint IntakeFaultPoint { get; set; }

    public IReadOnlyCollection<ExecutionThreadEnvelopeWave10> Threads
    {
        get
        {
            lock (_sync)
                return Array.AsReadOnly(_executionStores.Values.SelectMany(x => x.Threads).ToArray());
        }
    }

    public IReadOnlyCollection<AuditEnvelope> AuditLog
    {
        get
        {
            lock (_sync)
                return Array.AsReadOnly(_intakeAudits.Concat(_executionStores.Values.SelectMany(x => x.ExecutionAuditLog)).ToArray());
        }
    }

    public IReadOnlyCollection<OutboxEnvelope> Outbox
    {
        get
        {
            lock (_sync)
                return Array.AsReadOnly(_executionStores.Values.SelectMany(x => x.ExecutionOutbox).ToArray());
        }
    }

    public IReadOnlyCollection<IdempotencyRecord> IdempotencyRecords
    {
        get
        {
            lock (_sync)
                return Array.AsReadOnly(_intakeIdempotency.Values
                    .Concat(_executionStores.Values.SelectMany(x => x.ExecutionIdempotencyRecords)).ToArray());
        }
    }

    public ValueTask<ExecutionThreadEnvelopeWave10?> GetExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_executionStores.TryGetValue(executionId, out var store))
                return ValueTask.FromResult<ExecutionThreadEnvelopeWave10?>(null);
            return store.GetExecutionAsync(executionId, cancellationToken);
        }
    }

    public ValueTask<IdempotencyRecord?> GetExecutionIdempotencyAsync(
        string commandName,
        string executionId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_executionStores.TryGetValue(executionId, out var store))
                return ValueTask.FromResult<IdempotencyRecord?>(null);
            return store.GetExecutionIdempotencyAsync(commandName, executionId, idempotencyKey, cancellationToken);
        }
    }

    public ValueTask<AuthorityResult> CommitExecutionCommandAsync(
        ExecutionCommandRequestWave10 request,
        ExecutionCommitWave10 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_executionStores.TryGetValue(request.Command.ExecutionId, out var store))
                return ValueTask.FromResult(AuthorityResult.Deny(
                    404,
                    "P1_EXECUTION_NOT_FOUND",
                    request.Command.CorrelationId));
            return store.CommitExecutionCommandAsync(request, commit, cancellationToken);
        }
    }

    public ValueTask<IdempotencyRecord?> GetExecutionIntakeIdempotencyAsync(
        string executionId,
        string sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_intakeIdempotency.TryGetValue(IntakeKey(executionId, sourceMessageId), out var value)
                ? value
                : null);
    }

    public ValueTask<AuthorityResult> CommitExecutionIntakeAsync(
        ExecutionHandoffIntakeRequestWave10 request,
        ExecutionHandoffIntakeCommitWave10 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var executionId = request.SourceEvidence.ExecutionId;
            var key = IntakeKey(executionId, request.SourceEvent.MessageId);

            if (_intakeIdempotency.TryGetValue(key, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", request.SourceEvent.CorrelationId));
                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = request.SourceEvent.CorrelationId
                });
            }

            if (_executionStores.TryGetValue(executionId, out var existingStore))
            {
                var existing = existingStore.Threads.Single();
                if (!SameThread(existing.Execution, commit.Thread.Execution))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_EXECUTION_HANDOFF_THREAD_CONFLICT", request.SourceEvent.CorrelationId));

                return ValueTask.FromResult(new AuthorityResult(
                    200,
                    "P1_EXECUTION_HANDOFF_ALREADY_MATERIALIZED",
                    true,
                    false,
                    true,
                    existing.Execution.Version,
                    request.SourceEvent.CorrelationId,
                    Array.Empty<string>()));
            }

            ValidateIntake(request, commit);
            ThrowIf(ExecutionIntakeWave10PersistenceFaultPoint.AfterThreadStaged);
            ThrowIf(ExecutionIntakeWave10PersistenceFaultPoint.AfterAuditStaged);

            var result = new AuthorityResult(
                200,
                "P1_EXECUTION_HANDOFF_MATERIALIZED",
                true,
                true,
                false,
                1,
                request.SourceEvent.CorrelationId,
                Array.Empty<string>());
            var idem = new IdempotencyRecord(
                "system.execution.materialize-from-handoff",
                executionId,
                request.SourceEvent.MessageId,
                request.Fingerprint,
                result);

            ThrowIf(ExecutionIntakeWave10PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(ExecutionIntakeWave10PersistenceFaultPoint.BeforeCommitPublish);

            _executionStores.Add(executionId, new ExecutionTransactionalStoreWave10(commit.Thread));
            _intakeAudits.Add(commit.Audit);
            _intakeIdempotency.Add(key, idem);
            return ValueTask.FromResult(result);
        }
    }

    private static void ValidateIntake(
        ExecutionHandoffIntakeRequestWave10 request,
        ExecutionHandoffIntakeCommitWave10 commit)
    {
        var source = request.SourceEvidence;
        var execution = commit.Thread.Execution;
        if (!string.Equals(execution.ExecutionId, source.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(execution.RecommendationId, source.RecommendationId, StringComparison.Ordinal)
            || !string.Equals(execution.CandidateId, source.CandidateId, StringComparison.Ordinal)
            || !string.Equals(execution.IdeaId, source.IdeaId, StringComparison.Ordinal)
            || execution.ApprovedIdeaVersion != source.ApprovedIdeaVersion
            || !string.Equals(execution.State, "PLANNING", StringComparison.Ordinal)
            || execution.Version != 1
            || commit.Thread.BenefitObligation is not null)
            throw new InvalidOperationException("P1_EXECUTION_HANDOFF_INTAKE_INVALID");

        if (!string.Equals(commit.Audit.AggregateId, execution.ExecutionId, StringComparison.Ordinal)
            || commit.Audit.EntityVersion != 1
            || !string.Equals(commit.Audit.CommandName, "system.execution.materialize-from-handoff", StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.SourceEvent.CorrelationId, StringComparison.Ordinal))
            throw new InvalidOperationException("P1_EXECUTION_HANDOFF_AUDIT_INVALID");
    }

    private static bool SameThread(ExecutionEnvelopeWave10 left, ExecutionEnvelopeWave10 right) =>
        string.Equals(left.ExecutionId, right.ExecutionId, StringComparison.Ordinal)
        && string.Equals(left.RecommendationId, right.RecommendationId, StringComparison.Ordinal)
        && string.Equals(left.CandidateId, right.CandidateId, StringComparison.Ordinal)
        && string.Equals(left.IdeaId, right.IdeaId, StringComparison.Ordinal)
        && left.ApprovedIdeaVersion == right.ApprovedIdeaVersion;

    private void ThrowIf(ExecutionIntakeWave10PersistenceFaultPoint point)
    {
        if (IntakeFaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave10 Execution intake persistence fault at {point}.");
    }

    private static string IntakeKey(string executionId, string sourceMessageId) =>
        $"system.execution.materialize-from-handoff\u001f{executionId}\u001f{sourceMessageId}";
}
