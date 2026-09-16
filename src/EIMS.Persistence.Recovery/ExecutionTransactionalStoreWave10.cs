using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum ExecutionWave10PersistenceFaultPoint
{
    None = 0,
    AfterThreadStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5
}

/// <summary>
/// Provider-neutral Wave 10 reference persistence. It proves atomic Execution semantics only.
/// It is not an Oracle adapter and is not physical OP-04 evidence.
/// </summary>
public sealed class ExecutionTransactionalStoreWave10 : IExecutionWave10Store
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExecutionThreadEnvelopeWave10> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();

    public ExecutionTransactionalStoreWave10(params ExecutionThreadEnvelopeWave10[] initialThreads)
    {
        foreach (var thread in initialThreads)
            _threads.Add(thread.Execution.ExecutionId, thread);
    }

    public PersistenceContractDescriptor Contract { get; } = PersistenceContractDescriptor.RecoveryBaseline();
    public ExecutionWave10PersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<ExecutionThreadEnvelopeWave10> Threads
    {
        get { lock (_sync) return Array.AsReadOnly(_threads.Values.ToArray()); }
    }

    public IReadOnlyCollection<AuditEnvelope> ExecutionAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> ExecutionOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> ExecutionIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<ExecutionThreadEnvelopeWave10?> GetExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_threads.TryGetValue(executionId, out var value) ? value : null);
    }

    public ValueTask<IdempotencyRecord?> GetExecutionIdempotencyAsync(
        string commandName,
        string executionId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(Key(commandName, executionId, idempotencyKey), out var value)
                ? value : null);
    }

    public ValueTask<AuthorityResult> CommitExecutionCommandAsync(
        ExecutionCommandRequestWave10 request,
        ExecutionCommitWave10 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var command = request.Command;
            var idemKey = Key(command.CommandName, command.ExecutionId, command.IdempotencyKey);
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

            if (!_threads.TryGetValue(command.ExecutionId, out var current)
                || current.Execution.Version != request.Before.Execution.Version
                || current.Execution.Version != command.ExpectedVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_EXECUTION_CONCURRENT_CONFLICT", command.CorrelationId));

            ValidateCommit(request, commit);
            ThrowIf(ExecutionWave10PersistenceFaultPoint.AfterThreadStaged);
            ThrowIf(ExecutionWave10PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(ExecutionWave10PersistenceFaultPoint.AfterOutboxStaged);

            var emitted = commit.OutboxEvents.Select(x => x.EventName).ToArray();
            var result = new AuthorityResult(
                200,
                "P1_EXECUTION_COMMAND_COMMITTED",
                true,
                true,
                false,
                commit.After.Execution.Version,
                command.CorrelationId,
                emitted);
            var idem = new IdempotencyRecord(
                command.CommandName,
                command.ExecutionId,
                command.IdempotencyKey,
                request.Fingerprint,
                result);

            ThrowIf(ExecutionWave10PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(ExecutionWave10PersistenceFaultPoint.BeforeCommitPublish);

            _threads[command.ExecutionId] = commit.After;
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency[idemKey] = idem;
            return ValueTask.FromResult(result);
        }
    }

    private static void ValidateCommit(
        ExecutionCommandRequestWave10 request,
        ExecutionCommitWave10 commit)
    {
        var before = request.Before.Execution;
        var after = commit.After.Execution;
        if (after.Version != before.Version + 1)
            throw new InvalidOperationException("P1_EXECUTION_VERSION_ADVANCE_INVALID");

        if (!string.Equals(after.ExecutionId, before.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(after.RecommendationId, before.RecommendationId, StringComparison.Ordinal)
            || !string.Equals(after.CandidateId, before.CandidateId, StringComparison.Ordinal)
            || !string.Equals(after.IdeaId, before.IdeaId, StringComparison.Ordinal)
            || after.ApprovedIdeaVersion != before.ApprovedIdeaVersion)
            throw new InvalidOperationException("P1_EXECUTION_DIGITAL_THREAD_BROKEN");

        if (!string.Equals(commit.Audit.CommandName, request.Command.CommandName, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.PersonId, request.Actor.PersonId, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.Assignment, request.Actor.AssignmentId, StringComparison.Ordinal)
            || commit.OutboxEvents.Count == 0
            || commit.OutboxEvents.Any(x => !string.Equals(x.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)))
            throw new InvalidOperationException("P1_EXECUTION_AUDIT_OUTBOX_INVALID");

        if (commit.After.BenefitObligation is not null)
        {
            var benefit = commit.After.BenefitObligation;
            if (!string.Equals(benefit.ExecutionId, after.ExecutionId, StringComparison.Ordinal)
                || !string.Equals(benefit.IdeaId, after.IdeaId, StringComparison.Ordinal)
                || benefit.ApprovedIdeaVersion != after.ApprovedIdeaVersion)
                throw new InvalidOperationException("P1_EXECUTION_BENEFIT_THREAD_BROKEN");
        }
    }

    private void ThrowIf(ExecutionWave10PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave10 Execution persistence fault at {point}.");
    }

    private static string Key(string command, string executionId, string key) =>
        $"{command}\u001f{executionId}\u001f{key}";
}
