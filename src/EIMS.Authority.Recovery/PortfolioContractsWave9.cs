namespace EIMS.Authority.Recovery;

public sealed record PortfolioEligibilityTrigger(
    string SourceEventName,
    string IdeaId,
    long ApprovedIdeaVersion,
    string IdeaStatus,
    int HardReadinessGaps,
    bool PolicyExcluded,
    string CorrelationId);

public sealed record PortfolioCandidateEnvelope(
    string CandidateId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record PortfolioAssignmentEnvelope(
    string AssignmentId,
    string CandidateId,
    string PortfolioId,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record PortfolioMembershipEnvelope(
    string MembershipId,
    string CandidateId,
    string AssignmentId,
    string PortfolioId,
    string Status,
    long Version,
    DateTimeOffset DecidedAt,
    string DecidedByPersonId,
    string AuthorityAssignmentId,
    string CorrelationId);

public sealed record ExecutionRecommendationEnvelope(
    string RecommendationId,
    string CandidateId,
    string MembershipId,
    string State,
    long Version,
    string? GovernanceDecisionRef,
    bool BaselineApproved,
    string? ApprovedBaselineRef,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record ExecutionHandoffEnvelope(
    string ExecutionId,
    string RecommendationId,
    string CandidateId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record PortfolioThreadEnvelope(
    PortfolioCandidateEnvelope Candidate,
    PortfolioAssignmentEnvelope? Assignment,
    PortfolioMembershipEnvelope? Membership,
    ExecutionRecommendationEnvelope? Recommendation,
    ExecutionHandoffEnvelope? Execution,
    long ThreadVersion);

public sealed record PortfolioCommandWave9(
    string CommandName,
    string CandidateId,
    long ExpectedThreadVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? PortfolioId = null,
    string? MembershipDecision = null,
    string? RecommendationId = null,
    string? GovernanceDecisionRef = null,
    string? ApprovedBaselineRef = null,
    string? RequestedScope = null);

public sealed record PortfolioEligibilityRequestWave9(
    PortfolioEligibilityTrigger Trigger,
    string Fingerprint);

public sealed record PortfolioCommandRequestWave9(
    PortfolioCommandWave9 Command,
    AuthorityActor Actor,
    PortfolioThreadEnvelope Before,
    CommandPolicy Policy,
    string Fingerprint);

public sealed record PortfolioCommitWave9(
    PortfolioThreadEnvelope After,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public interface IPortfolioWave9Store
{
    ValueTask<PortfolioThreadEnvelope?> GetByIdeaVersionAsync(
        string ideaId,
        long approvedIdeaVersion,
        CancellationToken cancellationToken = default);

    ValueTask<PortfolioThreadEnvelope?> GetByCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyRecord?> GetPortfolioIdempotencyAsync(
        string commandName,
        string aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitEligibilityAsync(
        PortfolioEligibilityRequestWave9 request,
        PortfolioCommitWave9 commit,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitPortfolioCommandAsync(
        PortfolioCommandRequestWave9 request,
        PortfolioCommitWave9 commit,
        CancellationToken cancellationToken = default);
}
