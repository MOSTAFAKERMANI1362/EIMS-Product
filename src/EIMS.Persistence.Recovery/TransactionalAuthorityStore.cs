using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public sealed class TransactionalAuthorityStore : IEvaluationWorkflowStore, IPersistenceEvidenceSource, IFaultInjectablePersistence
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AggregateSnapshot> _aggregates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();
    private readonly List<DomainDecisionEnvelope> _decisions = new();
    private readonly List<G01DecisionSnapshotEnvelope> _decisionSnapshots = new();
    private readonly List<EvaluationPlanEnvelope> _evaluationPlans = new();
    private readonly List<EvaluationAssignmentEnvelope> _evaluationAssignments = new();
    private readonly List<AssessmentSnapshotEnvelope> _assessmentSnapshots = new();
    private readonly List<G04AssessmentEnvelope> _g04Assessments = new();

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

    public IReadOnlyCollection<G01DecisionSnapshotEnvelope> DecisionSnapshots
    {
        get { lock (_sync) return Array.AsReadOnly(_decisionSnapshots.ToArray()); }
    }

    public IReadOnlyCollection<EvaluationPlanEnvelope> EvaluationPlans
    {
        get { lock (_sync) return Array.AsReadOnly(_evaluationPlans.ToArray()); }
    }

    public IReadOnlyCollection<EvaluationAssignmentEnvelope> EvaluationAssignments
    {
        get { lock (_sync) return Array.AsReadOnly(_evaluationAssignments.ToArray()); }
    }

    public IReadOnlyCollection<AssessmentSnapshotEnvelope> AssessmentSnapshots
    {
        get { lock (_sync) return Array.AsReadOnly(_assessmentSnapshots.ToArray()); }
    }

    public IReadOnlyCollection<G04AssessmentEnvelope> G04Assessments
    {
        get { lock (_sync) return Array.AsReadOnly(_g04Assessments.ToArray()); }
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

    public ValueTask<EvaluationPlanEnvelope?> GetEvaluationPlanAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_evaluationPlans.FirstOrDefault(x => string.Equals(x.PlanId, planId, StringComparison.Ordinal)));
    }

    public ValueTask<EvaluationAssignmentEnvelope?> GetEvaluationAssignmentAsync(
        string evaluationAssignmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_evaluationAssignments.FirstOrDefault(x => string.Equals(x.AssignmentId, evaluationAssignmentId, StringComparison.Ordinal)));
    }

    public ValueTask<IReadOnlyCollection<EvaluationAssignmentEnvelope>> GetEvaluationAssignmentsForPlanAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var items = _evaluationAssignments
                .Where(x => string.Equals(x.PlanId, planId, StringComparison.Ordinal))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<EvaluationAssignmentEnvelope>>(Array.AsReadOnly(items));
        }
    }

    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_g04Assessments.FirstOrDefault(x => string.Equals(x.PlanId, planId, StringComparison.Ordinal)));
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
            var decisionSnapshot = commit.DecisionSnapshot is not null
                ? SnapshotDecisionSnapshot(commit.DecisionSnapshot)
                : CreateG01DecisionSnapshot(request, commit, decisions);
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
            var oldDecisionSnapshotCount = _decisionSnapshots.Count;
            var oldPlanCount = _evaluationPlans.Count;
            var oldAssignmentCount = _evaluationAssignments.Count;
            var oldAuditCount = _audits.Count;
            var oldOutboxCount = _outbox.Count;

            try
            {
                _aggregates[commit.After.AggregateId] = SnapshotAggregate(commit.After);
                ThrowIf(PersistenceFaultPoint.AfterStateStaged);

                _decisions.AddRange(decisions);
                if (decisionSnapshot is not null)
                    _decisionSnapshots.Add(decisionSnapshot);
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

                while (_decisionSnapshots.Count > oldDecisionSnapshotCount)
                    _decisionSnapshots.RemoveAt(_decisionSnapshots.Count - 1);

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

    public ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(
        EvaluationCompletionRequest request,
        EvaluationCompletionCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            const string commandName = "evaluation-assignments.complete";
            var idempotencyKey = Key(commandName, request.Command.IdeaId, request.Command.IdempotencyKey);
            if (_idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_IDEMPOTENCY_CONFLICT", request.Command.CorrelationId));

                return ValueTask.FromResult(prior.Result with
                {
                    IdempotentReplay = true,
                    StateMutated = false,
                    CorrelationId = request.Command.CorrelationId
                });
            }

            if (!_aggregates.TryGetValue(request.Idea.AggregateId, out var currentIdea))
                return ValueTask.FromResult(AuthorityResult.Deny(404, "P2_AGGREGATE_NOT_FOUND", request.Command.CorrelationId));

            var planIndex = _evaluationPlans.FindIndex(x => string.Equals(x.PlanId, request.Plan.PlanId, StringComparison.Ordinal));
            var assignmentIndex = _evaluationAssignments.FindIndex(x => string.Equals(x.AssignmentId, request.Assignment.AssignmentId, StringComparison.Ordinal));
            if (planIndex < 0 || assignmentIndex < 0)
                return ValueTask.FromResult(AuthorityResult.Deny(404, "P2_EVALUATION_CONTEXT_NOT_FOUND", request.Command.CorrelationId));

            var currentPlan = _evaluationPlans[planIndex];
            var currentAssignment = _evaluationAssignments[assignmentIndex];

            if (currentIdea.Version != request.Idea.Version || currentIdea.Version != request.Command.ExpectedIdeaVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_VERSION_CONFLICT", request.Command.CorrelationId));
            if (currentPlan.PlanVersion != request.Plan.PlanVersion || currentPlan.PlanVersion != request.Command.ExpectedPlanVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_EVALUATION_PLAN_VERSION_CONFLICT", request.Command.CorrelationId));
            if (currentAssignment.AssignmentVersion != request.Assignment.AssignmentVersion
                || currentAssignment.AssignmentVersion != request.Command.ExpectedAssignmentVersion)
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_EVALUATION_ASSIGNMENT_VERSION_CONFLICT", request.Command.CorrelationId));

            if (!string.Equals(currentAssignment.State, "PENDING", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentPlan.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_EVALUATION_CONTEXT_NOT_MUTABLE", request.Command.CorrelationId));

            var error = ValidateEvaluationCompletionShape(request, commit, currentIdea, currentPlan, currentAssignment);
            if (error is not null)
                return ValueTask.FromResult(AuthorityResult.Deny(500, error, request.Command.CorrelationId));

            if (_assessmentSnapshots.Any(x => string.Equals(x.SnapshotId, commit.AssessmentSnapshot.SnapshotId, StringComparison.Ordinal)
                || string.Equals(x.EvaluationAssignmentId, currentAssignment.AssignmentId, StringComparison.Ordinal)))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_ASSESSMENT_SNAPSHOT", request.Command.CorrelationId));

            if (commit.G04Assessment is not null
                && (_g04Assessments.Any(x => string.Equals(x.AssessmentId, commit.G04Assessment.AssessmentId, StringComparison.Ordinal))
                    || _g04Assessments.Any(x => string.Equals(x.PlanId, currentPlan.PlanId, StringComparison.Ordinal))))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_G04_ASSESSMENT", request.Command.CorrelationId));

            if (_audits.Any(x => string.Equals(x.AuditId, commit.Audit.AuditId, StringComparison.Ordinal)))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", request.Command.CorrelationId));

            var outboxEvents = commit.OutboxEvents.Select(SnapshotOutbox).ToArray();
            if (outboxEvents.Length is < 1 or > 2
                || outboxEvents.GroupBy(x => x.MessageId, StringComparer.Ordinal).Any(g => g.Count() > 1)
                || outboxEvents.Any(x => _outbox.Any(existing => string.Equals(existing.MessageId, x.MessageId, StringComparison.Ordinal))))
                return ValueTask.FromResult(AuthorityResult.Deny(409, "P2_COMPLETION_OUTBOX_INVALID", request.Command.CorrelationId));

            var expectedReadiness = _evaluationAssignments
                .Where(x => string.Equals(x.PlanId, currentPlan.PlanId, StringComparison.Ordinal) && x.Required)
                .Select(x => string.Equals(x.AssignmentId, currentAssignment.AssignmentId, StringComparison.Ordinal) ? commit.AssignmentAfter : x)
                .All(x => string.Equals(x.State, "COMPLETED", StringComparison.OrdinalIgnoreCase));

            if (expectedReadiness != string.Equals(commit.PlanAfter.State, "READY_FOR_G04_DECISION", StringComparison.Ordinal))
                return ValueTask.FromResult(AuthorityResult.Deny(500, "P2_EVALUATION_READINESS_MISMATCH", request.Command.CorrelationId));
            if (expectedReadiness != (commit.G04Assessment is not null))
                return ValueTask.FromResult(AuthorityResult.Deny(500, "P2_G04_ASSESSMENT_READINESS_MISMATCH", request.Command.CorrelationId));

            var emittedEvents = outboxEvents.Select(x => x.EventName).ToArray();
            var result = new AuthorityResult(
                200,
                "P2_EVALUATION_COMPLETION_ATOMIC_COMMIT",
                Allowed: true,
                StateMutated: true,
                IdempotentReplay: false,
                NewVersion: commit.AssignmentAfter.AssignmentVersion,
                CorrelationId: request.Command.CorrelationId,
                EmittedEvents: Array.AsReadOnly(emittedEvents));

            var idempotency = new IdempotencyRecord(
                commandName,
                request.Command.IdeaId,
                request.Command.IdempotencyKey,
                request.IdempotencyFingerprint,
                result);

            var oldPlan = currentPlan;
            var oldAssignment = currentAssignment;
            var oldSnapshotCount = _assessmentSnapshots.Count;
            var oldG04Count = _g04Assessments.Count;
            var oldAuditCount = _audits.Count;
            var oldOutboxCount = _outbox.Count;

            try
            {
                _evaluationAssignments[assignmentIndex] = commit.AssignmentAfter;
                ThrowIf(PersistenceFaultPoint.AfterEvaluationAssignmentCompletionStaged);

                _assessmentSnapshots.Add(commit.AssessmentSnapshot);
                ThrowIf(PersistenceFaultPoint.AfterAssessmentSnapshotStaged);

                _evaluationPlans[planIndex] = commit.PlanAfter;
                ThrowIf(PersistenceFaultPoint.AfterEvaluationPlanReadinessStaged);

                if (commit.G04Assessment is not null)
                    _g04Assessments.Add(commit.G04Assessment);
                ThrowIf(PersistenceFaultPoint.AfterG04AssessmentStaged);

                _audits.Add(SnapshotAudit(commit.Audit));
                ThrowIf(PersistenceFaultPoint.AfterAuditStaged);

                _outbox.AddRange(outboxEvents);
                ThrowIf(PersistenceFaultPoint.AfterCompletionOutboxStaged);

                _idempotency.Add(idempotencyKey, idempotency);
                ThrowIf(PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowIf(PersistenceFaultPoint.BeforeCommitPublish);

                return ValueTask.FromResult(result);
            }
            catch
            {
                _evaluationAssignments[assignmentIndex] = oldAssignment;
                _evaluationPlans[planIndex] = oldPlan;

                while (_assessmentSnapshots.Count > oldSnapshotCount)
                    _assessmentSnapshots.RemoveAt(_assessmentSnapshots.Count - 1);
                while (_g04Assessments.Count > oldG04Count)
                    _g04Assessments.RemoveAt(_g04Assessments.Count - 1);
                while (_audits.Count > oldAuditCount)
                    _audits.RemoveAt(_audits.Count - 1);
                while (_outbox.Count > oldOutboxCount)
                    _outbox.RemoveAt(_outbox.Count - 1);

                _idempotency.Remove(idempotencyKey);
                throw;
            }
        }
    }

    private static string? ValidateEvaluationCompletionShape(
        EvaluationCompletionRequest request,
        EvaluationCompletionCommit commit,
        AggregateSnapshot currentIdea,
        EvaluationPlanEnvelope currentPlan,
        EvaluationAssignmentEnvelope currentAssignment)
    {
        var after = commit.AssignmentAfter;
        if (!string.Equals(after.AssignmentId, currentAssignment.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(after.PlanId, currentPlan.PlanId, StringComparison.Ordinal)
            || !string.Equals(after.IdeaId, currentIdea.AggregateId, StringComparison.Ordinal)
            || after.IdeaVersion != currentIdea.Version
            || !string.Equals(after.Role, currentAssignment.Role, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(after.Scope, currentAssignment.Scope, StringComparison.OrdinalIgnoreCase)
            || after.Required != currentAssignment.Required)
            return "P2_EVALUATION_ASSIGNMENT_LINK_MISMATCH";

        if (after.AssignmentVersion != currentAssignment.AssignmentVersion + 1
            || !string.Equals(after.State, "COMPLETED", StringComparison.Ordinal)
            || after.CompletedAt is null
            || string.IsNullOrWhiteSpace(after.CompletedByPersonId)
            || string.IsNullOrWhiteSpace(after.AuthorityAssignmentId)
            || string.IsNullOrWhiteSpace(after.AssessmentSchemaId)
            || string.IsNullOrWhiteSpace(after.AssessmentSchemaVersion)
            || string.IsNullOrWhiteSpace(after.AssessmentOutcome))
            return "P2_EVALUATION_ASSIGNMENT_COMPLETION_SHAPE_INVALID";

        if (!string.Equals(after.CompletedByPersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(after.AuthorityAssignmentId, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase))
            return "P2_EVALUATION_ASSIGNMENT_AUTHORITY_MISMATCH";

        var planAfter = commit.PlanAfter;
        if (!string.Equals(planAfter.PlanId, currentPlan.PlanId, StringComparison.Ordinal)
            || !string.Equals(planAfter.IdeaId, currentPlan.IdeaId, StringComparison.Ordinal)
            || planAfter.IdeaVersion != currentPlan.IdeaVersion
            || planAfter.PlanVersion != currentPlan.PlanVersion + 1
            || planAfter.CreatedAt != currentPlan.CreatedAt
            || !string.Equals(planAfter.CorrelationId, currentPlan.CorrelationId, StringComparison.Ordinal))
            return "P2_EVALUATION_PLAN_COMPLETION_SHAPE_INVALID";

        if (planAfter.State is not ("ACTIVE" or "READY_FOR_G04_DECISION"))
            return "P2_EVALUATION_PLAN_STATE_INVALID";

        var snapshot = commit.AssessmentSnapshot;
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId)
            || !string.Equals(snapshot.EvaluationAssignmentId, after.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(snapshot.PlanId, after.PlanId, StringComparison.Ordinal)
            || !string.Equals(snapshot.IdeaId, after.IdeaId, StringComparison.Ordinal)
            || snapshot.IdeaVersion != after.IdeaVersion
            || !string.Equals(snapshot.Role, after.Role, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Scope, after.Scope, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.SchemaId, after.AssessmentSchemaId, StringComparison.Ordinal)
            || !string.Equals(snapshot.SchemaVersion, after.AssessmentSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(snapshot.Outcome, after.AssessmentOutcome, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(snapshot.NormalizedAssessmentJson)
            || string.IsNullOrWhiteSpace(snapshot.ContentSha256))
            return "P2_ASSESSMENT_SNAPSHOT_SHAPE_INVALID";

        if (!string.Equals(snapshot.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.AuthorityAssignmentId, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase))
            return "P2_ASSESSMENT_SNAPSHOT_AUTHORITY_MISMATCH";

        if (!string.Equals(commit.Audit.AggregateId, currentIdea.AggregateId, StringComparison.Ordinal)
            || commit.Audit.EntityVersion != currentIdea.Version
            || !string.Equals(commit.Audit.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(commit.Audit.Assignment, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(commit.Audit.CommandName, "evaluation-assignments.complete", StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
            return "P2_EVALUATION_AUDIT_MISMATCH";

        if (after.CompletedAt != commit.Audit.Timestamp
            || snapshot.CreatedAt != commit.Audit.Timestamp)
            return "P2_EVALUATION_TIMESTAMP_MISMATCH";

        if (string.Equals(planAfter.State, "READY_FOR_G04_DECISION", StringComparison.Ordinal))
        {
            if (planAfter.ReadyAt != commit.Audit.Timestamp || commit.G04Assessment is null)
                return "P2_G04_READINESS_SHAPE_INVALID";
        }
        else if (planAfter.ReadyAt is not null || commit.G04Assessment is not null)
            return "P2_G04_PREMATURE_ASSESSMENT";

        if (commit.G04Assessment is not null)
        {
            var g04 = commit.G04Assessment;
            if (string.IsNullOrWhiteSpace(g04.AssessmentId)
                || !string.Equals(g04.PlanId, planAfter.PlanId, StringComparison.Ordinal)
                || !string.Equals(g04.IdeaId, currentIdea.AggregateId, StringComparison.Ordinal)
                || g04.IdeaVersion != currentIdea.Version
                || g04.PlanVersion != planAfter.PlanVersion
                || !string.Equals(g04.State, "PENDING", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(g04.RequiredAssignmentSnapshotSha256)
                || g04.CreatedAt != commit.Audit.Timestamp
                || !string.Equals(g04.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
                return "P2_G04_ASSESSMENT_SHAPE_INVALID";
        }

        var events = commit.OutboxEvents.ToArray();
        if (events.Length == 0
            || !string.Equals(events[0].EventName, "EvaluationAssignmentCompleted.v1", StringComparison.Ordinal)
            || (commit.G04Assessment is null && events.Length != 1)
            || (commit.G04Assessment is not null && (events.Length != 2 || !events.Any(x => string.Equals(x.EventName, "G04DecisionAssessmentCreated.v1", StringComparison.Ordinal)))))
            return "P2_EVALUATION_EVENT_SET_INVALID";

        foreach (var item in events)
        {
            if (!string.Equals(item.AggregateId, currentIdea.AggregateId, StringComparison.Ordinal)
                || item.AggregateVersion != currentIdea.Version
                || !string.Equals(item.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
                || item.OccurredAt != commit.Audit.Timestamp)
                return "P2_EVALUATION_EVENT_CONTEXT_MISMATCH";
        }

        return null;
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

    private static G01DecisionSnapshotEnvelope? CreateG01DecisionSnapshot(
        MutationRequest request,
        MutationCommit commit,
        IReadOnlyCollection<DomainDecisionEnvelope> decisions)
    {
        if (!string.Equals(request.Command.CommandName, "g01.decide", StringComparison.OrdinalIgnoreCase)
            || decisions.Count == 0)
            return null;

        var decision = decisions.Single();
        using var document = JsonDocument.Parse(request.Command.RawBody);
        var root = document.RootElement;

        var outcome = decision.Outcome.Trim().ToUpperInvariant();
        var observationVersion = ReadLong(root, "observationVersion") ?? request.Before.Version;
        var gateOutcome = ReadString(root, "gateOutcome")
            ?? (string.Equals(outcome, "APPROVE", StringComparison.Ordinal) ? "G01_COMPLETE" : "G01_INCOMPLETE");
        var ruleResults = root.TryGetProperty("ruleResults", out var rules) && rules.ValueKind == JsonValueKind.Object
            ? rules
            : default;
        var ruleExecutions = new[] { "R02", "R03", "R04", "R05" }
            .Select(ruleId => new G01RuleExecutionSnapshot(
                ruleId,
                ruleResults.ValueKind == JsonValueKind.Object && ruleResults.TryGetProperty(ruleId, out var value)
                    ? value.GetString() ?? "NOT_PROVIDED"
                    : "NOT_PROVIDED"))
            .ToArray();

        var role = request.Actor.Roles.FirstOrDefault(x => string.Equals(x, "INTAKE_STEWARD", StringComparison.OrdinalIgnoreCase))
            ?? request.Actor.Roles.FirstOrDefault()
            ?? string.Empty;
        var scope = request.Command.RequestedScope
            ?? request.Before.Scope
            ?? string.Empty;
        var authorization = new G01DecisionAuthorizationContext(
            request.Actor.PersonId, role, "G01.DECIDE", scope, request.Actor.AssignmentId);

        var ruleSetId = decision.Facts is not null && decision.Facts.TryGetValue("RuleSetId", out var rsid)
            ? rsid : "G01-INQ";
        var ruleSetVersion = decision.Facts is not null && decision.Facts.TryGetValue("RuleSetVersion", out var rsv)
            ? rsv : "1.0";
        var reasonCode = ReadString(root, "reasonCode");
        var comment = ReadString(root, "comment") ?? decision.Note;
        var createdAt = commit.Audit.Timestamp;
        var snapshotId = $"SNAP-{decision.DecisionId}";
        var fingerprintMaterial = string.Join("
", new[]
        {
            snapshotId, decision.DecisionId, commit.After.AggregateId,
            observationVersion.ToString(CultureInfo.InvariantCulture), ruleSetId, ruleSetVersion,
            gateOutcome, string.Join("|", ruleExecutions.Select(x => $"{x.RuleId}={x.Outcome}")),
            authorization.PrincipalId, authorization.Role, authorization.Capability,
            authorization.Scope, authorization.AssignmentId, outcome, reasonCode ?? string.Empty,
            comment ?? string.Empty, createdAt.ToString("O", CultureInfo.InvariantCulture), "1.0"
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintMaterial))).ToLowerInvariant();

        return new G01DecisionSnapshotEnvelope(
            snapshotId, decision.DecisionId, commit.After.AggregateId, observationVersion,
            ruleSetId, ruleSetVersion, gateOutcome, Array.AsReadOnly(ruleExecutions),
            authorization, outcome, reasonCode, comment, createdAt, "1.0", fingerprint);
    }

    private static G01DecisionSnapshotEnvelope SnapshotDecisionSnapshot(G01DecisionSnapshotEnvelope snapshot) =>
        snapshot with
        {
            RuleExecutions = Array.AsReadOnly(snapshot.RuleExecutions.ToArray()),
            AuthorizationContext = snapshot.AuthorizationContext with { }
        };

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

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
