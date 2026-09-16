using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum PortfolioWave9PersistenceFaultPoint
{
    None = 0,
    AfterThreadStaged = 1,
    AfterAuditStaged = 2,
    AfterOutboxStaged = 3,
    AfterIdempotencyStaged = 4,
    BeforeCommitPublish = 5
}

/// <summary>
/// Provider-neutral Wave 9 reference persistence. It proves atomic Portfolio semantics only.
/// It is not an Oracle adapter and must not be treated as physical OP-04 evidence.
/// </summary>
public sealed class PortfolioTransactionalStoreWave9 : IPortfolioWave9Store
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PortfolioThreadEnvelope> _threadsByCandidate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _candidateByIdeaVersion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();

    public PersistenceContractDescriptor Contract { get; } = PersistenceContractDescriptor.RecoveryBaseline();
    public PortfolioWave9PersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<PortfolioThreadEnvelope> Threads
    {
        get { lock (_sync) return Array.AsReadOnly(_threadsByCandidate.Values.ToArray()); }
    }

    public IReadOnlyCollection<AuditEnvelope> PortfolioAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> PortfolioOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> PortfolioIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<PortfolioThreadEnvelope?> GetByIdeaVersionAsync(
        string ideaId,
        long approvedIdeaVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = IdeaVersionKey(ideaId, approvedIdeaVersion);
            if (!_candidateByIdeaVersion.TryGetValue(key, out var candidateId))
                return ValueTask.FromResult<PortfolioThreadEnvelope?>(null);
            return ValueTask.FromResult<PortfolioThreadEnvelope?>(_threadsByCandidate[candidateId]);
        }
    }

    public ValueTask<PortfolioThreadEnvelope?> GetByCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_threadsByCandidate.TryGetValue(candidateId, out var thread) ? thread : null);
    }

    public ValueTask<IdempotencyRecord?> GetPortfolioIdempotencyAsync(
        string commandName,
        string aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(IdemKey(commandName, aggregateId, idempotencyKey), out var record)
                ? record : null);
    }

    public ValueTask<AuthorityResult> CommitEligibilityAsync(
        PortfolioEligibilityRequestWave9 request,
        PortfolioCommitWave9 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var trigger = request.Trigger;
            var ideaKey = IdeaVersionKey(trigger.IdeaId, trigger.ApprovedIdeaVersion);
            var idemKey = IdemKey("portfolio.evaluate-eligibility-from-approved-idea", trigger.IdeaId, request.Fingerprint);

            if (_idempotency.TryGetValue(idemKey, out var prior))
                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = trigger.CorrelationId
                });

            if (_candidateByIdeaVersion.TryGetValue(ideaKey, out var existingCandidate))
            {
                var existing = _threadsByCandidate[existingCandidate];
                return ValueTask.FromResult(new AuthorityResult(
                    200, "P1_PORTFOLIO_ELIGIBILITY_ALREADY_MATERIALIZED", true, false, true,
                    existing.ThreadVersion, trigger.CorrelationId, Array.Empty<string>()));
            }

            ValidateEligibilityCommit(request, commit);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterThreadStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged);

            var events = commit.OutboxEvents.Select(x => x.EventName).ToArray();
            var result = new AuthorityResult(
                200, "P1_PORTFOLIO_CANDIDATE_CREATED", true, true, false,
                commit.After.ThreadVersion, trigger.CorrelationId, events);
            var idempotency = new IdempotencyRecord(
                "portfolio.evaluate-eligibility-from-approved-idea",
                trigger.IdeaId,
                request.Fingerprint,
                request.Fingerprint,
                result);

            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.BeforeCommitPublish);

            _threadsByCandidate.Add(commit.After.Candidate.CandidateId, commit.After);
            _candidateByIdeaVersion.Add(ideaKey, commit.After.Candidate.CandidateId);
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency.Add(idemKey, idempotency);
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<AuthorityResult> CommitPortfolioCommandAsync(
        PortfolioCommandRequestWave9 request,
        PortfolioCommitWave9 commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var command = request.Command;
            var key = IdemKey(command.CommandName, command.CandidateId, command.IdempotencyKey);
            if (_idempotency.TryGetValue(key, out var prior))
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

            if (!_threadsByCandidate.TryGetValue(command.CandidateId, out var current)
                || current.ThreadVersion != request.Before.ThreadVersion
                || current.ThreadVersion != command.ExpectedThreadVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P1_PORTFOLIO_CONCURRENT_CONFLICT", command.CorrelationId));

            ValidateCommandCommit(request, commit);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterThreadStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterOutboxStaged);

            var events = commit.OutboxEvents.Select(x => x.EventName).ToArray();
            var result = new AuthorityResult(
                200, "P1_PORTFOLIO_COMMAND_COMMITTED", true, true, false,
                commit.After.ThreadVersion, command.CorrelationId, events);
            var idempotency = new IdempotencyRecord(
                command.CommandName, command.CandidateId, command.IdempotencyKey,
                request.Fingerprint, result);

            ThrowIf(PortfolioWave9PersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(PortfolioWave9PersistenceFaultPoint.BeforeCommitPublish);

            _threadsByCandidate[command.CandidateId] = commit.After;
            _audits.Add(commit.Audit);
            _outbox.AddRange(commit.OutboxEvents);
            _idempotency[key] = idempotency;
            return ValueTask.FromResult(result);
        }
    }

    private static void ValidateEligibilityCommit(
        PortfolioEligibilityRequestWave9 request,
        PortfolioCommitWave9 commit)
    {
        if (commit.After.ThreadVersion != 1
            || commit.After.Assignment is not null
            || commit.After.Membership is not null
            || commit.After.Recommendation is not null
            || commit.After.Execution is not null
            || !string.Equals(commit.After.Candidate.IdeaId, request.Trigger.IdeaId, StringComparison.Ordinal)
            || commit.After.Candidate.ApprovedIdeaVersion != request.Trigger.ApprovedIdeaVersion
            || !string.Equals(commit.After.Candidate.State, "UNASSIGNED_CANDIDATE", StringComparison.Ordinal))
            throw new InvalidOperationException("P1_PORTFOLIO_ELIGIBILITY_COMMIT_INVALID");

        var eventNames = commit.OutboxEvents.Select(x => x.EventName).ToHashSet(StringComparer.Ordinal);
        if (!eventNames.SetEquals(new[] { "PortfolioEligibilityEvaluated.v1", "PortfolioIntakeCandidateCreated.v1" }))
            throw new InvalidOperationException("P1_PORTFOLIO_ELIGIBILITY_EVENTS_INVALID");
    }

    private static void ValidateCommandCommit(
        PortfolioCommandRequestWave9 request,
        PortfolioCommitWave9 commit)
    {
        if (commit.After.ThreadVersion != request.Before.ThreadVersion + 1)
            throw new InvalidOperationException("P1_PORTFOLIO_THREAD_VERSION_INVALID");
        if (!string.Equals(commit.After.Candidate.CandidateId, request.Before.Candidate.CandidateId, StringComparison.Ordinal)
            || !string.Equals(commit.After.Candidate.IdeaId, request.Before.Candidate.IdeaId, StringComparison.Ordinal)
            || commit.After.Candidate.ApprovedIdeaVersion != request.Before.Candidate.ApprovedIdeaVersion)
            throw new InvalidOperationException("P1_PORTFOLIO_DIGITAL_THREAD_BROKEN");
        if (!string.Equals(commit.Audit.CommandName, request.Command.CommandName, StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || commit.OutboxEvents.Any(x => !string.Equals(x.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)))
            throw new InvalidOperationException("P1_PORTFOLIO_CORRELATION_INVALID");
        if (commit.OutboxEvents.Count == 0)
            throw new InvalidOperationException("P1_PORTFOLIO_EVENT_REQUIRED");
    }

    private void ThrowIf(PortfolioWave9PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave9 Portfolio persistence fault at {point}.");
    }

    private static string IdeaVersionKey(string ideaId, long version) => $"{ideaId}\u001f{version}";
    private static string IdemKey(string command, string aggregate, string key) => $"{command}\u001f{aggregate}\u001f{key}";
}
