using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum G04FinalDecisionPersistenceFaultPoint
{
    None = 0,
    AfterIdeaStaged = 1,
    AfterPlanStaged = 2,
    AfterAssessmentStaged = 3,
    AfterEvidenceStaged = 4,
    AfterAuditStaged = 5,
    AfterOutboxStaged = 6,
    AfterIdempotencyStaged = 7,
    BeforeCommitPublish = 8
}

/// <summary>
/// Provider-neutral Wave 8 reference persistence. It proves atomic final-decision semantics only;
/// it is not the production Oracle adapter and contains no Oracle-specific DDL or credentials.
/// </summary>
public sealed class G04FinalDecisionTransactionalStore : IG04FinalDecisionStore
{
    private readonly object _sync = new();
    private AggregateSnapshot _idea;
    private EvaluationPlanEnvelope _plan;
    private G04AssessmentEnvelope _assessment;
    private readonly G04CommitteeSnapshotEnvelope? _committeeSnapshot;
    private readonly G04CommitteeStateEnvelope? _committeeState;
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<G04FinalDecisionEvidenceEnvelope> _evidence = new();
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();

    public G04FinalDecisionTransactionalStore(
        AggregateSnapshot idea,
        EvaluationPlanEnvelope plan,
        G04AssessmentEnvelope assessment,
        G04CommitteeSnapshotEnvelope? committeeSnapshot = null,
        G04CommitteeStateEnvelope? committeeState = null)
    {
        _idea = idea;
        _plan = plan;
        _assessment = assessment;
        _committeeSnapshot = committeeSnapshot;
        _committeeState = committeeState;
    }

    public PersistenceContractDescriptor Contract { get; } = PersistenceContractDescriptor.RecoveryBaseline();
    public G04FinalDecisionPersistenceFaultPoint FaultPoint { get; set; }

    public AggregateSnapshot CurrentIdea { get { lock (_sync) return _idea; } }
    public EvaluationPlanEnvelope CurrentPlan { get { lock (_sync) return _plan; } }
    public G04AssessmentEnvelope CurrentAssessment { get { lock (_sync) return _assessment; } }
    public IReadOnlyCollection<G04FinalDecisionEvidenceEnvelope> FinalDecisionEvidence
    {
        get { lock (_sync) return Array.AsReadOnly(_evidence.ToArray()); }
    }
    public IReadOnlyCollection<AuditEnvelope> FinalDecisionAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }
    public IReadOnlyCollection<OutboxEnvelope> FinalDecisionOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }
    public IReadOnlyCollection<IdempotencyRecord> FinalDecisionIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<AggregateSnapshot?> GetIdeaAsync(string ideaId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult<AggregateSnapshot?>(
                string.Equals(ideaId, _idea.AggregateId, StringComparison.Ordinal) ? _idea : null);
    }

    public ValueTask<EvaluationPlanEnvelope?> GetPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult<EvaluationPlanEnvelope?>(
                string.Equals(planId, _plan.PlanId, StringComparison.Ordinal) ? _plan : null);
    }

    public ValueTask<G04AssessmentEnvelope?> GetAssessmentAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult<G04AssessmentEnvelope?>(
                string.Equals(assessmentId, _assessment.AssessmentId, StringComparison.Ordinal) ? _assessment : null);
    }

    public ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _committeeSnapshot is not null && string.Equals(_committeeSnapshot.AssessmentId, assessmentId, StringComparison.Ordinal)
                ? _committeeSnapshot : null);
    }

    public ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _committeeState is not null && string.Equals(_committeeState.AssessmentId, assessmentId, StringComparison.Ordinal)
                ? _committeeState : null);
    }

    public ValueTask<IdempotencyRecord?> GetFinalDecisionIdempotencyAsync(
        string assessmentId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(Key(assessmentId, idempotencyKey), out var value) ? value : null);
    }

    public ValueTask<AuthorityResult> CommitFinalDecisionAsync(
        G04FinalDecisionRequest request,
        G04FinalDecisionCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var key = Key(request.Assessment.AssessmentId, request.Command.IdempotencyKey);
            if (_idempotency.TryGetValue(key, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(
                        409, "P1_IDEMPOTENCY_CONFLICT", request.Command.CorrelationId));
                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = request.Command.CorrelationId
                });
            }

            if (!MatchesCurrentRequest(request))
                return ValueTask.FromResult(AuthorityResult.Deny(
                    409, "P1_G04_FINAL_CONCURRENT_CONFLICT", request.Command.CorrelationId));

            ValidateCommit(request, commit);

            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterIdeaStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterPlanStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterAssessmentStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterEvidenceStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterAuditStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterOutboxStaged);

            var result = new AuthorityResult(
                200,
                "P1_G04_FINAL_DECISION_COMMITTED",
                Allowed: true,
                StateMutated: true,
                IdempotentReplay: false,
                NewVersion: commit.IdeaAfter.Version,
                CorrelationId: request.Command.CorrelationId,
                EmittedEvents: new[] { commit.Outbox.EventName });
            var idempotency = new IdempotencyRecord(
                "g04.final-decision", request.Idea.AggregateId, request.Command.IdempotencyKey,
                request.IdempotencyFingerprint, result);

            ThrowIf(G04FinalDecisionPersistenceFaultPoint.AfterIdempotencyStaged);
            ThrowIf(G04FinalDecisionPersistenceFaultPoint.BeforeCommitPublish);

            _idea = commit.IdeaAfter;
            _plan = commit.PlanAfter;
            _assessment = commit.AssessmentAfter;
            _evidence.Add(commit.Evidence);
            _audits.Add(commit.Audit);
            _outbox.Add(commit.Outbox);
            _idempotency[key] = idempotency;

            return ValueTask.FromResult(result);
        }
    }

    private bool MatchesCurrentRequest(G04FinalDecisionRequest request)
    {
        var currentTechnical = _idea.StateMutationVersion ?? _idea.Version;
        var requestTechnical = request.Idea.StateMutationVersion ?? request.Idea.Version;
        return string.Equals(_idea.AggregateId, request.Idea.AggregateId, StringComparison.Ordinal)
            && _idea.Version == request.Idea.Version
            && currentTechnical == requestTechnical
            && string.Equals(_idea.State, request.Idea.State, StringComparison.Ordinal)
            && string.Equals(_plan.PlanId, request.Plan.PlanId, StringComparison.Ordinal)
            && _plan.PlanVersion == request.Plan.PlanVersion
            && string.Equals(_plan.State, request.Plan.State, StringComparison.Ordinal)
            && string.Equals(_assessment.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            && _assessment.DecisionVersion == request.Assessment.DecisionVersion
            && string.Equals(_assessment.State, request.Assessment.State, StringComparison.Ordinal);
    }

    private static void ValidateCommit(G04FinalDecisionRequest request, G04FinalDecisionCommit commit)
    {
        var beforeTechnical = request.Idea.StateMutationVersion ?? request.Idea.Version;
        var afterTechnical = commit.IdeaAfter.StateMutationVersion ?? commit.IdeaAfter.Version;
        if (afterTechnical != beforeTechnical + 1)
            throw new InvalidOperationException("P1_G04_FINAL_TECHNICAL_VERSION_INVALID");
        if (commit.AssessmentAfter.DecisionVersion != request.Assessment.DecisionVersion + 1
            || !string.Equals(commit.AssessmentAfter.State, "DECIDED", StringComparison.Ordinal))
            throw new InvalidOperationException("P1_G04_FINAL_ASSESSMENT_MUTATION_INVALID");
        if (!string.Equals(commit.Evidence.DecisionId, commit.Outbox.Payload?["decisionEvidenceId"], StringComparison.Ordinal))
            throw new InvalidOperationException("P1_G04_FINAL_EVIDENCE_EVENT_LINK_INVALID");
        if (!string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Outbox.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Evidence.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
            throw new InvalidOperationException("P1_G04_FINAL_CORRELATION_INVALID");
    }

    private void ThrowIf(G04FinalDecisionPersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected Wave8 final-decision persistence fault at {point}.");
    }

    private static string Key(string assessmentId, string key) => $"{assessmentId}\u001f{key}";
}
