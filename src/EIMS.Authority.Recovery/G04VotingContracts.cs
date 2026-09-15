namespace EIMS.Authority.Recovery;

public sealed record G04GovernanceProfile(
    string GovernanceProfileId,
    string GovernanceProfileVersion,
    int QuorumRequired,
    string VoteRule,
    string ChairPersonId,
    string ApprovalAuthority,
    string ApprovalRef);

public sealed record G04CommitteeMemberEnvelope(
    string PersonId,
    string DisplayName,
    IReadOnlyCollection<string> SourceRoles,
    IReadOnlyCollection<string> AuthorityAssignmentIds);

public sealed record G04CommitteeSnapshotEnvelope(
    string SnapshotId,
    string AssessmentId,
    string IdeaId,
    long IdeaVersion,
    string Scope,
    string DecisionRoute,
    string GovernanceProfileId,
    string GovernanceProfileVersion,
    int QuorumRequired,
    string VoteRule,
    string ChairPersonId,
    string ApprovalAuthority,
    string ApprovalRef,
    IReadOnlyCollection<G04CommitteeMemberEnvelope> Members,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record G04CommitteeStateEnvelope(
    string AssessmentId,
    string SnapshotId,
    int CommitteeVersion,
    string VotingStageState,
    bool QuorumReached,
    bool ApprovalRuleSatisfied,
    int EffectiveVoteCount,
    int ApproveCount,
    int RejectCount,
    DateTimeOffset UpdatedAt,
    string CorrelationId,
    DateTimeOffset? CompletedAt = null);

public sealed record G04VoteRevisionEnvelope(
    string VoteRevisionId,
    string AssessmentId,
    string SnapshotId,
    int CommitteeVersion,
    string PersonId,
    string AuthorityAssignmentId,
    string Vote,
    string Note,
    int RevisionNumber,
    string? SupersedesVoteRevisionId,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record G04EffectiveVoteEnvelope(
    string AssessmentId,
    string PersonId,
    string VoteRevisionId,
    string Vote,
    int RevisionNumber,
    DateTimeOffset UpdatedAt);

public sealed record G04VoteCommand(
    string PlanId,
    string AssessmentId,
    long ExpectedIdeaVersion,
    int ExpectedCommitteeVersion,
    string IdempotencyKey,
    string CorrelationId,
    string Vote,
    string Note,
    string? RequestedScope = null);

public sealed record G04VoteRequest(
    G04VoteCommand Command,
    AuthorityActor Actor,
    AggregateSnapshot Idea,
    G04AssessmentEnvelope Assessment,
    G04CommitteeSnapshotEnvelope? SnapshotBefore,
    G04CommitteeStateEnvelope? StateBefore,
    IReadOnlyCollection<G04EffectiveVoteEnvelope> EffectiveVotesBefore,
    string IdempotencyFingerprint);

public sealed record G04VoteCommit(
    G04CommitteeSnapshotEnvelope SnapshotAfter,
    G04CommitteeStateEnvelope StateAfter,
    G04VoteRevisionEnvelope VoteRevision,
    G04EffectiveVoteEnvelope EffectiveVoteAfter,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public interface IG04GovernanceProfileProvider
{
    ValueTask<G04GovernanceProfile?> ResolveAsync(
        G04AssessmentEnvelope assessment,
        AggregateSnapshot idea,
        CancellationToken cancellationToken = default);
}

public interface IG04CommitteeMembershipResolver
{
    ValueTask<IReadOnlyCollection<G04CommitteeMemberEnvelope>> ResolveAsync(
        G04AssessmentEnvelope assessment,
        AggregateSnapshot idea,
        G04GovernanceProfile profile,
        CancellationToken cancellationToken = default);
}

public interface IG04VotingStore
{
    ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default);
    ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default);
    ValueTask<IdempotencyRecord?> GetVoteIdempotencyAsync(string assessmentId, string idempotencyKey, CancellationToken cancellationToken = default);
    ValueTask<G04CommitteeSnapshotEnvelope?> GetCommitteeSnapshotAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<G04CommitteeStateEnvelope?> GetCommitteeStateAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyCollection<G04EffectiveVoteEnvelope>> GetEffectiveVotesAsync(string assessmentId, CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitG04VoteAsync(G04VoteRequest request, G04VoteCommit commit, CancellationToken cancellationToken = default);
}
