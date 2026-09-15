using System.Collections.ObjectModel;
using EIMS.Authority.Recovery;

namespace EIMS.Persistence.Recovery;

public enum G04VotingPersistenceFaultPoint
{
    None = 0,
    AfterSnapshotStaged = 1,
    AfterVoteRevisionStaged = 2,
    AfterEffectiveProjectionStaged = 3,
    AfterCommitteeStateStaged = 4,
    AfterAuditStaged = 5,
    AfterOutboxStaged = 6,
    AfterIdempotencyStaged = 7,
    BeforeCommitPublish = 8
}

public sealed record G04VotingPersistenceContractDescriptor(
    string ContractId,
    string Version,
    bool FrozenSnapshotRequired,
    bool AppendOnlyVoteRevisionRequired,
    bool EffectiveProjectionRequired,
    bool OptimisticCommitteeVersionRequired,
    bool ServerIdempotencyRequired,
    bool AtomicSnapshotVoteProjectionStateAuditOutboxIdempotencyRequired,
    bool PhysicalOracleBound)
{
    public bool IsLogicalContractReady =>
        FrozenSnapshotRequired
        && AppendOnlyVoteRevisionRequired
        && EffectiveProjectionRequired
        && OptimisticCommitteeVersionRequired
        && ServerIdempotencyRequired
        && AtomicSnapshotVoteProjectionStateAuditOutboxIdempotencyRequired;

    public static G04VotingPersistenceContractDescriptor RecoveryBaseline() => new(
        "EIMS-P2-G04-VOTING-RECOVERY",
        "1.0",
        true, true, true, true, true, true,
        PhysicalOracleBound: false);
}

public interface IG04VotingEvidenceSource
{
    G04VotingPersistenceContractDescriptor VotingContract { get; }
    IReadOnlyCollection<G04CommitteeSnapshotEnvelope> CommitteeSnapshots { get; }
    IReadOnlyCollection<G04CommitteeStateEnvelope> CommitteeStates { get; }
    IReadOnlyCollection<G04VoteRevisionEnvelope> VoteRevisions { get; }
    IReadOnlyCollection<G04EffectiveVoteEnvelope> EffectiveVotes { get; }
    IReadOnlyCollection<AuditEnvelope> VotingAuditLog { get; }
    IReadOnlyCollection<OutboxEnvelope> VotingOutbox { get; }
    IReadOnlyCollection<IdempotencyRecord> VotingIdempotencyRecords { get; }
}

public sealed class G04VotingTransactionalStore(
    IEvaluationWorkflowStore contextStore,
    G04VotingPersistenceContractDescriptor? contract = null)
    : IG04VotingStore, IG04VotingEvidenceSource
{
    private readonly object _sync = new();
    private readonly IEvaluationWorkflowStore _contextStore = contextStore;
    private readonly List<G04CommitteeSnapshotEnvelope> _snapshots = new();
    private readonly Dictionary<string, G04CommitteeStateEnvelope> _states = new(StringComparer.Ordinal);
    private readonly List<G04VoteRevisionEnvelope> _revisions = new();
    private readonly Dictionary<string, G04EffectiveVoteEnvelope> _effective = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AuditEnvelope> _audits = new();
    private readonly List<OutboxEnvelope> _outbox = new();
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);

    public G04VotingPersistenceContractDescriptor VotingContract { get; } = contract ?? G04VotingPersistenceContractDescriptor.RecoveryBaseline();
    public G04VotingPersistenceFaultPoint FaultPoint { get; set; }

    public IReadOnlyCollection<G04CommitteeSnapshotEnvelope> CommitteeSnapshots
    {
        get { lock (_sync) return Array.AsReadOnly(_snapshots.ToArray()); }
    }

    public IReadOnlyCollection<G04CommitteeStateEnvelope> CommitteeStates
    {
        get { lock (_sync) return Array.AsReadOnly(_states.Values.ToArray()); }
    }

    public IReadOnlyCollection<G04VoteRevisionEnvelope> VoteRevisions
    {
        get { lock (_sync) return Array.AsReadOnly(_revisions.ToArray()); }
    }

    public IReadOnlyCollection<G04EffectiveVoteEnvelope> EffectiveVotes
    {
        get { lock (_sync) return Array.AsReadOnly(_effective.Values.ToArray()); }
    }

    public IReadOnlyCollection<AuditEnvelope> VotingAuditLog
    {
        get { lock (_sync) return Array.AsReadOnly(_audits.ToArray()); }
    }

    public IReadOnlyCollection<OutboxEnvelope> VotingOutbox
    {
        get { lock (_sync) return Array.AsReadOnly(_outbox.ToArray()); }
    }

    public IReadOnlyCollection<IdempotencyRecord> VotingIdempotencyRecords
    {
        get { lock (_sync) return Array.AsReadOnly(_idempotency.Values.ToArray()); }
    }

    public ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default) =>
        _contextStore.GetAggregateAsync(aggregateId, cancellationToken);

    public ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        _contextStore.GetG04AssessmentForPlanAsync(planId, cancellationToken);

    public ValueTask<IdempotencyRecord?> GetVoteIdempotencyAsync(
        string assessmentId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_idempotency.TryGetValue(IdempotencyKey(assessmentId, idempotencyKey), out var record) ? record : null);
    }

    public ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(
        string assessmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_snapshots.SingleOrDefault(x => string.Equals(x.AssessmentId, assessmentId, StringComparison.Ordinal)));
    }

    public ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(
        string assessmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return ValueTask.FromResult(_states.TryGetValue(assessmentId, out var state) ? state : null);
    }

    public ValueTask<IReadOnlyCollection<G04EffectiveVoteEnvelope>> GetEffectiveVotesAsync(
        string assessmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var items = _effective.Values.Where(x => string.Equals(x.AssessmentId, assessmentId, StringComparison.Ordinal)).ToArray();
            return ValueTask.FromResult<IReadOnlyCollection<G04EffectiveVoteEnvelope>>(Array.AsReadOnly(items));
        }
    }

    public async ValueTask<AuthorityResult> CommitG04VoteAsync(
        G04VoteRequest request,
        G04VoteCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var liveIdea = await _contextStore.GetAggregateAsync(request.Idea.AggregateId, cancellationToken);
        var liveAssessment = await _contextStore.GetG04AssessmentForPlanAsync(request.Command.PlanId, cancellationToken);
        if (liveIdea is null || liveAssessment is null)
            return AuthorityResult.Deny(404, "P2_G04_CONTEXT_NOT_FOUND", request.Command.CorrelationId);
        if (liveIdea.Version != request.Idea.Version
            || liveIdea.Version != request.Command.ExpectedIdeaVersion
            || !string.Equals(liveAssessment.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(liveAssessment.State, "PENDING", StringComparison.OrdinalIgnoreCase)
            || liveAssessment.IdeaVersion != liveIdea.Version)
            return AuthorityResult.Deny(409, "P2_G04_CONTEXT_VERSION_CONFLICT", request.Command.CorrelationId);

        lock (_sync)
        {
            var idempotencyKey = IdempotencyKey(request.Assessment.AssessmentId, request.Command.IdempotencyKey);
            if (_idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P2_IDEMPOTENCY_CONFLICT", request.Command.CorrelationId);
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = request.Command.CorrelationId };
            }

            var currentSnapshot = _snapshots.SingleOrDefault(x => string.Equals(x.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal));
            _states.TryGetValue(request.Assessment.AssessmentId, out var currentState);
            if ((currentSnapshot is null) != (currentState is null))
                return AuthorityResult.Deny(500, "P2_G04_COMMITTEE_CONTEXT_INCONSISTENT", request.Command.CorrelationId);

            if (!SameOptionalSnapshot(currentSnapshot, request.SnapshotBefore)
                || !SameOptionalState(currentState, request.StateBefore))
                return AuthorityResult.Deny(409, "P2_G04_COMMITTEE_CONTEXT_CHANGED", request.Command.CorrelationId);

            var currentVersion = currentState?.CommitteeVersion ?? 0;
            if (currentVersion != request.Command.ExpectedCommitteeVersion)
                return AuthorityResult.Deny(409, "P2_G04_COMMITTEE_VERSION_CONFLICT", request.Command.CorrelationId);
            if (currentState is not null && string.Equals(currentState.VotingStageState, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return AuthorityResult.Deny(409, "P2_G04_VOTING_STAGE_COMPLETED", request.Command.CorrelationId);

            var currentEffective = _effective.Values
                .Where(x => string.Equals(x.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal))
                .OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var requestEffective = request.EffectiveVotesBefore.OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!currentEffective.SequenceEqual(requestEffective))
                return AuthorityResult.Deny(409, "P2_G04_EFFECTIVE_PROJECTION_CHANGED", request.Command.CorrelationId);

            var shapeError = ValidateCommitShape(request, commit, currentSnapshot, currentState, currentEffective);
            if (shapeError is not null)
                return AuthorityResult.Deny(500, shapeError, request.Command.CorrelationId);

            if (_revisions.Any(x => string.Equals(x.VoteRevisionId, commit.VoteRevision.VoteRevisionId, StringComparison.Ordinal)))
                return AuthorityResult.Deny(409, "P2_DUPLICATE_G04_VOTE_REVISION_ID", request.Command.CorrelationId);
            if (_audits.Any(x => string.Equals(x.AuditId, commit.Audit.AuditId, StringComparison.Ordinal)))
                return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", request.Command.CorrelationId);

            var events = commit.OutboxEvents.ToArray();
            if (events.Length is < 1 or > 2
                || events.GroupBy(x => x.MessageId, StringComparer.Ordinal).Any(g => g.Count() > 1)
                || events.Any(x => _outbox.Any(existing => string.Equals(existing.MessageId, x.MessageId, StringComparison.Ordinal))))
                return AuthorityResult.Deny(409, "P2_G04_OUTBOX_INVALID", request.Command.CorrelationId);

            var result = new AuthorityResult(
                200,
                "P2_G04_VOTE_ATOMIC_COMMIT",
                Allowed: true,
                StateMutated: true,
                IdempotentReplay: false,
                NewVersion: commit.StateAfter.CommitteeVersion,
                CorrelationId: request.Command.CorrelationId,
                EmittedEvents: Array.AsReadOnly(events.Select(x => x.EventName).ToArray()));
            var idempotency = new IdempotencyRecord(
                "g04.vote", request.Assessment.AssessmentId, request.Command.IdempotencyKey,
                request.IdempotencyFingerprint, result);

            var oldSnapshotCount = _snapshots.Count;
            var oldRevisionCount = _revisions.Count;
            var oldAuditCount = _audits.Count;
            var oldOutboxCount = _outbox.Count;
            var hadState = _states.TryGetValue(request.Assessment.AssessmentId, out var oldState);
            var effectiveKey = EffectiveKey(request.Assessment.AssessmentId, request.Actor.PersonId);
            var hadEffective = _effective.TryGetValue(effectiveKey, out var oldEffective);

            try
            {
                if (currentSnapshot is null)
                    _snapshots.Add(SnapshotCommittee(commit.SnapshotAfter));
                ThrowIf(G04VotingPersistenceFaultPoint.AfterSnapshotStaged);

                _revisions.Add(commit.VoteRevision);
                ThrowIf(G04VotingPersistenceFaultPoint.AfterVoteRevisionStaged);

                _effective[effectiveKey] = commit.EffectiveVoteAfter;
                ThrowIf(G04VotingPersistenceFaultPoint.AfterEffectiveProjectionStaged);

                _states[request.Assessment.AssessmentId] = commit.StateAfter;
                ThrowIf(G04VotingPersistenceFaultPoint.AfterCommitteeStateStaged);

                _audits.Add(SnapshotAudit(commit.Audit));
                ThrowIf(G04VotingPersistenceFaultPoint.AfterAuditStaged);

                _outbox.AddRange(events.Select(SnapshotOutbox));
                ThrowIf(G04VotingPersistenceFaultPoint.AfterOutboxStaged);

                _idempotency.Add(idempotencyKey, idempotency);
                ThrowIf(G04VotingPersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowIf(G04VotingPersistenceFaultPoint.BeforeCommitPublish);

                return result;
            }
            catch
            {
                while (_snapshots.Count > oldSnapshotCount) _snapshots.RemoveAt(_snapshots.Count - 1);
                while (_revisions.Count > oldRevisionCount) _revisions.RemoveAt(_revisions.Count - 1);
                while (_audits.Count > oldAuditCount) _audits.RemoveAt(_audits.Count - 1);
                while (_outbox.Count > oldOutboxCount) _outbox.RemoveAt(_outbox.Count - 1);

                if (hadState) _states[request.Assessment.AssessmentId] = oldState!;
                else _states.Remove(request.Assessment.AssessmentId);

                if (hadEffective) _effective[effectiveKey] = oldEffective!;
                else _effective.Remove(effectiveKey);

                _idempotency.Remove(idempotencyKey);
                throw;
            }
        }
    }

    private static string? ValidateCommitShape(
        G04VoteRequest request,
        G04VoteCommit commit,
        G04CommitteeSnapshotEnvelope? currentSnapshot,
        G04CommitteeStateEnvelope? currentState,
        IReadOnlyCollection<G04EffectiveVoteEnvelope> currentEffective)
    {
        var snapshot = commit.SnapshotAfter;
        if (!string.Equals(snapshot.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(snapshot.IdeaId, request.Idea.AggregateId, StringComparison.Ordinal)
            || snapshot.IdeaVersion != request.Idea.Version
            || !string.Equals(snapshot.Scope, request.Idea.Scope, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.DecisionRoute, "G04_COMMITTEE", StringComparison.Ordinal)
            || snapshot.QuorumRequired < 1
            || snapshot.Members.Count < snapshot.QuorumRequired)
            return "P2_G04_SNAPSHOT_SHAPE_INVALID";

        if (currentSnapshot is not null && !SameSnapshot(currentSnapshot, snapshot))
            return "P2_G04_FROZEN_SNAPSHOT_CHANGED";

        var expectedNextVersion = (currentState?.CommitteeVersion ?? 0) + 1;
        var state = commit.StateAfter;
        if (!string.Equals(state.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(state.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
            || state.CommitteeVersion != expectedNextVersion
            || state.VotingStageState is not ("OPEN" or "COMPLETED")
            || state.EffectiveVoteCount != state.ApproveCount + state.RejectCount
            || state.EffectiveVoteCount < 1
            || state.EffectiveVoteCount > snapshot.Members.Count
            || state.UpdatedAt != commit.Audit.Timestamp
            || (string.Equals(state.VotingStageState, "COMPLETED", StringComparison.Ordinal) != (state.CompletedAt is not null)))
            return "P2_G04_COMMITTEE_STATE_SHAPE_INVALID";

        var revision = commit.VoteRevision;
        var prior = currentEffective.SingleOrDefault(x => string.Equals(x.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(revision.VoteRevisionId)
            || !string.Equals(revision.AssessmentId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(revision.SnapshotId, snapshot.SnapshotId, StringComparison.Ordinal)
            || revision.CommitteeVersion != expectedNextVersion
            || !string.Equals(revision.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(revision.AuthorityAssignmentId, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase)
            || revision.Vote is not ("APPROVE" or "REJECT")
            || revision.Note.Trim().Length < 10
            || revision.RevisionNumber != (prior?.RevisionNumber ?? 0) + 1
            || !string.Equals(revision.SupersedesVoteRevisionId, prior?.VoteRevisionId, StringComparison.Ordinal)
            || revision.CreatedAt != commit.Audit.Timestamp)
            return "P2_G04_VOTE_REVISION_SHAPE_INVALID";

        var effective = commit.EffectiveVoteAfter;
        if (!string.Equals(effective.AssessmentId, revision.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(effective.PersonId, revision.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(effective.VoteRevisionId, revision.VoteRevisionId, StringComparison.Ordinal)
            || !string.Equals(effective.Vote, revision.Vote, StringComparison.Ordinal)
            || effective.RevisionNumber != revision.RevisionNumber
            || effective.UpdatedAt != revision.CreatedAt)
            return "P2_G04_EFFECTIVE_VOTE_SHAPE_INVALID";

        if (!string.Equals(commit.Audit.AggregateId, request.Assessment.AssessmentId, StringComparison.Ordinal)
            || commit.Audit.EntityVersion != expectedNextVersion
            || !string.Equals(commit.Audit.PersonId, request.Actor.PersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(commit.Audit.Assignment, request.Actor.AssignmentId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(commit.Audit.CommandName, "g04.vote", StringComparison.Ordinal)
            || !string.Equals(commit.Audit.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal))
            return "P2_G04_AUDIT_MISMATCH";

        var events = commit.OutboxEvents.ToArray();
        var completed = string.Equals(state.VotingStageState, "COMPLETED", StringComparison.Ordinal);
        if (events.Length != (completed ? 2 : 1)
            || !string.Equals(events[0].EventName, "G04CommitteeVoteRecorded.v1", StringComparison.Ordinal)
            || (completed && !events.Any(x => string.Equals(x.EventName, "G04CommitteeVotingCompleted.v1", StringComparison.Ordinal))))
            return "P2_G04_EVENT_SET_INVALID";

        foreach (var item in events)
        {
            if (!string.Equals(item.AggregateId, request.Assessment.AssessmentId, StringComparison.Ordinal)
                || item.AggregateVersion != expectedNextVersion
                || !string.Equals(item.CorrelationId, request.Command.CorrelationId, StringComparison.Ordinal)
                || item.OccurredAt != commit.Audit.Timestamp)
                return "P2_G04_EVENT_CONTEXT_MISMATCH";
            if (item.Payload is not null
                && item.Payload.Keys.Any(k => k.Contains("note", StringComparison.OrdinalIgnoreCase)
                    || k.Contains("reason", StringComparison.OrdinalIgnoreCase)
                    || k.Contains("memberSnapshot", StringComparison.OrdinalIgnoreCase)
                    || k.Contains("governanceSnapshot", StringComparison.OrdinalIgnoreCase)
                    || k.Contains("assessmentAnswer", StringComparison.OrdinalIgnoreCase)))
                return "P2_G04_EVENT_DATA_MINIMIZATION_VIOLATION";
        }

        return null;
    }

    private static bool SameOptionalSnapshot(G04CommitteeSnapshotEnvelope? left, G04CommitteeSnapshotEnvelope? right) =>
        left is null ? right is null : right is not null && SameSnapshot(left, right);

    private static bool SameOptionalState(G04CommitteeStateEnvelope? left, G04CommitteeStateEnvelope? right) =>
        left is null ? right is null : right is not null && left == right;

    private static bool SameSnapshot(G04CommitteeSnapshotEnvelope left, G04CommitteeSnapshotEnvelope right)
    {
        if (!string.Equals(left.SnapshotId, right.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(left.AssessmentId, right.AssessmentId, StringComparison.Ordinal)
            || !string.Equals(left.IdeaId, right.IdeaId, StringComparison.Ordinal)
            || left.IdeaVersion != right.IdeaVersion
            || !string.Equals(left.Scope, right.Scope, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(left.DecisionRoute, right.DecisionRoute, StringComparison.Ordinal)
            || !string.Equals(left.GovernanceProfileId, right.GovernanceProfileId, StringComparison.Ordinal)
            || !string.Equals(left.GovernanceProfileVersion, right.GovernanceProfileVersion, StringComparison.Ordinal)
            || left.QuorumRequired != right.QuorumRequired
            || !string.Equals(left.VoteRule, right.VoteRule, StringComparison.Ordinal)
            || !string.Equals(left.ChairPersonId, right.ChairPersonId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(left.ApprovalAuthority, right.ApprovalAuthority, StringComparison.Ordinal)
            || !string.Equals(left.ApprovalRef, right.ApprovalRef, StringComparison.Ordinal)
            || left.CreatedAt != right.CreatedAt)
            return false;

        var lm = left.Members.OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase).ToArray();
        var rm = right.Members.OrderBy(x => x.PersonId, StringComparer.OrdinalIgnoreCase).ToArray();
        if (lm.Length != rm.Length) return false;
        for (var i = 0; i < lm.Length; i++)
        {
            if (!string.Equals(lm[i].PersonId, rm[i].PersonId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(lm[i].DisplayName, rm[i].DisplayName, StringComparison.Ordinal)
                || !lm[i].SourceRoles.SequenceEqual(rm[i].SourceRoles)
                || !lm[i].AuthorityAssignmentIds.SequenceEqual(rm[i].AuthorityAssignmentIds))
                return false;
        }
        return true;
    }

    private static G04CommitteeSnapshotEnvelope SnapshotCommittee(G04CommitteeSnapshotEnvelope snapshot) =>
        snapshot with
        {
            Members = Array.AsReadOnly(snapshot.Members.Select(x => x with
            {
                SourceRoles = Array.AsReadOnly(x.SourceRoles.ToArray()),
                AuthorityAssignmentIds = Array.AsReadOnly(x.AuthorityAssignmentIds.ToArray())
            }).ToArray())
        };

    private static AuditEnvelope SnapshotAudit(AuditEnvelope audit) =>
        audit with { Roles = Array.AsReadOnly(audit.Roles.ToArray()) };

    private static OutboxEnvelope SnapshotOutbox(OutboxEnvelope outbox)
    {
        IReadOnlyDictionary<string, string>? payload = null;
        if (outbox.Payload is not null)
            payload = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(outbox.Payload, StringComparer.Ordinal));
        return outbox with { Payload = payload };
    }

    private void ThrowIf(G04VotingPersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected G04 voting persistence failure at {point}.");
    }

    private static string IdempotencyKey(string assessmentId, string key) => $"g04.vote|{assessmentId}|{key}";
    private static string EffectiveKey(string assessmentId, string personId) => $"{assessmentId}|{personId}";
}
