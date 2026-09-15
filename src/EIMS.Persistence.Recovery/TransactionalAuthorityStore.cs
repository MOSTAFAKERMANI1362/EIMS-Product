using System.Collections.ObjectModel;
using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public sealed class TransactionalAuthorityStore : IAuthorityStore, IPersistenceEvidenceSource, IFaultInjectablePersistence
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AggregateSnapshot> _aggregates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();
    private readonly List<DomainDecisionEnvelope> _decisions = new();
    private readonly List<EvaluationPlanEnvelope> _evaluationPlans = new();
    private readonly List<EvaluationAssignmentEnvelope> _evaluationAssignments = new();

    public TransactionalAuthorityStore(
        PersistenceContractDescriptor? contract = null,
        params AggregateSnapshot[] aggregates)
    {
        Contract = contract ?? PersistenceContractDescriptor.RecoveryBaseline();
        foreach (var aggregate in aggregates)
        {
            var snapshot = SnapshotAggregate(aggregate);
            _aggregates[snapshot.AggregateId] = snapshot;
        }
    }

    public PersistenceContractDescriptor Contract { get; }
    public PersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<AuditEnvelope> AuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> Outbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> IdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public IReadOnlyCollection<DomainDecisionEnvelope> DomainDecisions
    {
        get { lock (_sync) return Array.AsReadOnly(_decisions.ToArray()); }
    }

    public IReadOnlyCollection<EvaluationPlanEnvelope> EvaluationPlans
    {
        get { lock (_sync) return Array.AsReadOnly(_evaluationPlans.ToArray()); }
    }

    public IReadOnlyCollection<EvaluationAssignmentEnvelope> EvaluationAssignments
    {
        get { lock (_sync) return Array.AsReadOnly(_evaluationAssignments.ToArray()); }
    }

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(
        string aggregateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_aggregates.TryGetValue(aggregateId, out var aggregate)
                ? SnapshotAggregate(aggregate)
                : null);
    }

    public ValueTask<IdempotencyRecord?> GetIdempotencyAsync(
        string commandName,
        string aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(Key(commandName, aggregateId, idempotencyKey), out var record) ? record : null);
    }

    public ValueTask<AuthorityResult> CommitAsync(
        MutationRequest request,
        MutationCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var idempotencyKey = Key(
                request.Command.CommandName,
                request.Command.AggregateId,
                request.Command.IdempotencyKey);

            if (_idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(
                        409,
                        "P2_IDEMPOTENCY_CONFLICT",
                        request.Command.CorrelationId,
                        "The same idempotency key is already committed with a different fingerprint."));

                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = request.Command.CorrelationId
                });
            }

            if (!_aggregates.TryGetValue(request.Before.AggregateId, out var current))
                return ValueTask.FromResult(AuthorityResult.Deny(
                    404,
                    "P2_AGGREGATE_NOT_FOUND",
                    request.Command.CorrelationId));

            if (request.Command.ExpectedVersion != request.Before.Version)
                return ValueTask.FromResult(AuthorityResult.Deny(
                    409,
                    "P2_REQUEST_VERSION_MISMATCH",
                    request.Command.CorrelationId));

            if (current.Version != request.Before.Version)
                return ValueTask.FromResult(AuthorityResult.Deny(
                    409,
                    "P2_VERSION_CONFLICT",
                    request.Command.CorrelationId,
                    $"Expected {request.Before.Version}; current {current.Version}."));

            var contractError = ValidateCommitShape(request, commit);
            if (contractError is not null)
                return ValueTask.FromResult(AuthorityResult.Deny(
                    500,
                    contractError,
                    request.Command.CorrelationId));

            var decisions = (commit.Decisions ?? Array.Empty<DomainDecisionEnvelope>())
                .Select(SnapshotDecision)
                .ToArray();
            if (decisions.GroupBy(x => x.DecisionId, StringComparer.Ordinal).Any(g => g.Count() > 1)
                || decisions.Any(d => _decisions.Any(existing => string.Equals(existing.DecisionId, d.DecisionId, StringComparison.Ordinal))))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_DECISION_ID", request.Command.CorrelationId));

            var evaluationPlan = commit.EvaluationPlan;
            var evaluationAssignments = (commit.EvaluationAssignments ?? Array.Empty<EvaluationAssignmentEnvelope>()).ToArray();
            if (evaluationPlan is not null
                && (_evaluationPlans.Any(x => string.Equals(x.PlanId, evaluationPlan.PlanId, StringComparison.Ordinal))
                    || _evaluationPlans.Any(x => string.Equals(x.IdeaId, evaluationPlan.IdeaId, StringComparison.OrdinalIgnoreCase)
                        && x.IdeaVersion == evaluationPlan.IdeaVersion)))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_EVALUATION_PLAN", request.Command.CorrelationId));

            if (evaluationAssignments.GroupBy(x => x.AssignmentId, StringComparer.Ordinal).Any(g => g.Count() > 1)
                || evaluationAssignments.Any(a => _evaluationAssignments.Any(existing => string.Equals(existing.AssignmentId, a.AssignmentId, StringComparison.Ordinal))))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_EVALUATION_ASSIGNMENT_ID", request.Command.CorrelationId));

            var audit = SnapshotAudit(commit.Audit);
            if (_audits.Any(x => string.Equals(x.AuditId, audit.AuditId, StringComparison.Ordinal)))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", request.Command.CorrelationId));

            var outbox = SnapshotOutbox(commit.Outbox);
            if (_outbox.Any(x => string.Equals(x.MessageId, outbox.MessageId, StringComparison.Ordinal)))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_OUTBOX_ID", request.Command.CorrelationId));

            var result = new AuthorityResult(
                200,
                "P2_ATOMIC_COMMIT",
                Allowed: true,
                StateMutated: true,
                IdempotentReplay: false,
                NewVersion: commit.After.Version,
                CorrelationId: request.Command.CorrelationId,
                EmittedEvents: Array.AsReadOnly(new[] { outbox.EventName }));

            var idempotency = new IdempotencyRecord(
                request.Command.CommandName,
                request.Command.AggregateId,
                request.Command.IdempotencyKey,
                request.IdempotencyFingerprint,
                result);

            var oldAggregate = current;
            var oldDecisionCount = _decisions.Count;
            var oldPlanCount = _evaluationPlans.Count;
            var oldAssignmentCount = _evaluationAssignments.Count;
            var oldAuditCount = _audits.Count;
            var oldOutboxCount = _outbox.Count;

            try
            {
                _aggregates[commit.After.AggregateId] = SnapshotAggregate(commit.After);
                ThrowIf(PersistenceFaultPoint.AfterStateStaged);

                _decisions.AddRange(decisions);
                ThrowIf(PersistenceFaultPoint.AfterDecisionStaged);

                if (evaluationPlan is not null)
                    _evaluationPlans.Add(evaluationPlan);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationPlanStaged);

                _evaluationAssignments.AddRange(evaluationAssignments);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationAssignmentsStaged);

                _audits.Add(audit);
                ThrowIf(PersistenceFaultPoint.AfterAuditStaged);

                _outbox.Add(outbox);
                ThrowIf(PersistenceFaultPoint.AfterOutboxStaged);

                _idempotency.Add(idempotencyKey, idempotency);
                ThrowIf(PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowIf(PersistenceFaultPoint.BeforeCommitPublish);

                return ValueTask.FromResult(result);
            }
            catch
            {
                _aggregates[oldAggregate.AggregateId] = oldAggregate;

                while (_decisions.Count > oldDecisionCount)
                    _decisions.RemoveAt(_decisions.Count - 1);

                while (_evaluationPlans.Count > oldPlanCount)
                    _evaluationPlans.RemoveAt(_evaluationPlans.Count - 1);

                while (_evaluationAssignments.Count > oldAssignmentCount)
                    _evaluationAssignments.RemoveAt(_evaluationAssignments.Count - 1);

                while (_audits.Count > oldAuditCount)
                    _audits.RemoveAt(_audits.Count - 1);

                while (_outbox.Count > oldOutboxCount)
                    _outbox.RemoveAt(_outbox.Count - 1);

                _idempotency.Remove(idempotencyKey);
                throw;
            }
        }
    }

    private static string? ValidateCommitShape(MutationRequest request, MutationCommit commit)
    {
        if (!string.Equals(request.Before.AggregateId, commit.After.AggregateId, StringComparison.Ordinal))
            return "P2_AGGREGATE_ID_MISMATCH";

        if (commit.After.Version != request.Before.Version + 1)
            return "P2_INVALID_NEXT_VERSION";

        if (!string.Equals(commit.Audit.AggregateId, commit.After.AggregateId, StringComparison.Ordinal)
            || commit.Audit.EntityVersion != commit.After.Version)
            return "P2_AUDIT_ENTITY_VERSION_MISMATCH";

        if (!string.Equals(commit.Outbox.AggregateId, commit.After.AggregateId, StringComparison.Ordinal)
            || commit.Outbox.AggregateVersion != commit.After.Version)
            return "P2_OUTBOX_ENTITY_VERSION_MISMATCH";

        if (!string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(commit.Outbox.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
            return "P2_CORRELATION_MISMATCH";

        if (!string.Equals(commit.Audit.CommandName, request.Command.CommandName, StringComparison.OrdinalIgnoreCase))
            return "P2_AUDIT_COMMAND_MISMATCH";

        if (commit.Outbox.OccurredAt != commit.Audit.Timestamp)
            return "P2_EVIDENCE_TIMESTAMP_MISMATCH";

        foreach (var decision in commit.Decisions ?? Array.Empty<DomainDecisionEnvelope>())
        {
            if (string.IsNullOrWhiteSpace(decision.DecisionId)
                || string.IsNullOrWhiteSpace(decision.DecisionType)
                || string.IsNullOrWhiteSpace(decision.Outcome))
                return "P2_DECISION_SHAPE_INVALID";

            if (!string.Equals(decision.AggregateId, commit.After.AggregateId, StringComparison.Ordinal)
                || decision.EntityVersion != commit.After.Version)
                return "P2_DECISION_ENTITY_VERSION_MISMATCH";

            if (!string.Equals(decision.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
                return "P2_DECISION_CORRELATION_MISMATCH";

            if (!string.Equals(decision.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(decision.AssignmentId, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase))
                return "P2_DECISION_AUTHORITY_MISMATCH";

            if (decision.Timestamp != commit.Audit.Timestamp)
                return "P2_DECISION_TIMESTAMP_MISMATCH";
        }

        var assignments = commit.EvaluationAssignments ?? Array.Empty<EvaluationAssignmentEnvelope>();
        if (commit.EvaluationPlan is null)
            return assignments.Count == 0 ? null : "P2_EVALUATION_ASSIGNMENTS_WITHOUT_PLAN";

        var plan = commit.EvaluationPlan;
        if (string.IsNullOrWhiteSpace(plan.PlanId)
            || string.IsNullOrWhiteSpace(plan.State)
            || plan.PlanVersion <= 0)
            return "P2_EVALUATION_PLAN_SHAPE_INVALID";

        if (!string.Equals(plan.IdeaId, commit.After.AggregateId, StringComparison.Ordinal)
            || plan.IdeaVersion != commit.After.Version)
            return "P2_EVALUATION_PLAN_IDEA_VERSION_MISMATCH";

        if (!string.Equals(plan.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
            || plan.CreatedAt != commit.Audit.Timestamp)
            return "P2_EVALUATION_PLAN_EVIDENCE_MISMATCH";

        if (assignments.Count == 0)
            return "P2_EVALUATION_ASSIGNMENTS_REQUIRED";

        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.AssignmentId)
                || string.IsNullOrWhiteSpace(assignment.Role)
                || string.IsNullOrWhiteSpace(assignment.Scope)
                || string.IsNullOrWhiteSpace(assignment.State))
                return "P2_EVALUATION_ASSIGNMENT_SHAPE_INVALID";

            if (!string.Equals(assignment.PlanId, plan.PlanId, StringComparison.Ordinal)
                || !string.Equals(assignment.IdeaId, plan.IdeaId, StringComparison.Ordinal)
                || assignment.IdeaVersion != plan.IdeaVersion)
                return "P2_EVALUATION_ASSIGNMENT_LINK_MISMATCH";

            if (!string.Equals(assignment.Scope, commit.After.Scope, StringComparison.OrdinalIgnoreCase))
                return "P2_EVALUATION_ASSIGNMENT_SCOPE_MISMATCH";

            if (!string.Equals(assignment.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
                || assignment.CreatedAt != commit.Audit.Timestamp)
                return "P2_EVALUATION_ASSIGNMENT_EVIDENCE_MISMATCH";
        }

        return null;
    }

    private static AggregateSnapshot SnapshotAggregate(AggregateSnapshot aggregate) =>
        aggregate with { RuleFacts = SnapshotFacts(aggregate.RuleFacts) };

    private static AuditEnvelope SnapshotAudit(AuditEnvelope audit) =>
        audit with { Roles = Array.AsReadOnly(audit.Roles.ToArray()) };

    private static OutboxEnvelope SnapshotOutbox(OutboxEnvelope outbox) =>
        outbox with { Payload = SnapshotFacts(outbox.Payload) };

    private static DomainDecisionEnvelope SnapshotDecision(DomainDecisionEnvelope decision) =>
        decision with { Facts = SnapshotFacts(decision.Facts) };

    private static IReadOnlyDictionary<string, string>? SnapshotFacts(IReadOnlyDictionary<string, string>? facts)
    {
        if (facts is null)
            return null;

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in facts)
            copy[pair.Key] = pair.Value;
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private void ThrowIf(PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected persistence failure at {point}.");
    }

    private static string Key(string commandName, string aggregateId, string idempotencyKey) =>
        $"{commandName}|{aggregateId}|{idempotencyKey}";
}
