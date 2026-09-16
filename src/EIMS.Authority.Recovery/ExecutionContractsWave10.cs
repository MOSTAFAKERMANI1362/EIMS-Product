namespace EIMS.Authority.Recovery;

public sealed record ExecutionCommandWave10(
    string CommandName,
    string ExecutionId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? RequestedScope = null,
    int? ProgressPercent = null,
    string? CompletionDossierRef = null,
    string? CompletionDecision = null,
    string? ReviewNote = null);

public sealed record ExecutionEnvelopeWave10(
    string ExecutionId,
    string RecommendationId,
    string CandidateId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    bool CharterApproved,
    bool PlanApproved,
    int ProgressPercent,
    string? CompletionDossierRef,
    bool CompletionApproved,
    string? CompletionSubmittedByPersonId,
    string? CompletionSubmittedByAssignmentId,
    string? CompletionReviewedByPersonId,
    string? CompletionReviewDecision,
    string? BenefitId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CorrelationId);

public sealed record BenefitObligationEnvelopeWave10(
    string BenefitId,
    string ExecutionId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record ExecutionThreadEnvelopeWave10(
    ExecutionEnvelopeWave10 Execution,
    BenefitObligationEnvelopeWave10? BenefitObligation);

public sealed record ExecutionOwnershipEvidenceWave10(
    string ExecutionId,
    string OwnerPersonId,
    string OwnerAssignmentId,
    string Scope,
    string EvidenceRef,
    string EvidenceVersion);

public interface IExecutionOwnershipEvidenceProviderWave10
{
    ValueTask<ExecutionOwnershipEvidenceWave10?> ResolveAsync(
        string executionId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticExecutionOwnershipEvidenceProviderWave10(
    params ExecutionOwnershipEvidenceWave10[] evidence) : IExecutionOwnershipEvidenceProviderWave10
{
    private readonly IReadOnlyDictionary<string, ExecutionOwnershipEvidenceWave10> _byExecution =
        evidence.ToDictionary(x => x.ExecutionId, StringComparer.Ordinal);

    public ValueTask<ExecutionOwnershipEvidenceWave10?> ResolveAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byExecution.TryGetValue(executionId, out var value) ? value : null);
    }
}

public sealed record BenefitAcceptanceEvidenceWave10(
    string BenefitId,
    bool AcceptedByBenefitOwner,
    string? BenefitOwnerPersonId,
    string? BenefitOwnerAssignmentId,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitAcceptanceEvidenceProviderWave10
{
    ValueTask<BenefitAcceptanceEvidenceWave10?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitAcceptanceEvidenceProviderWave10(
    params BenefitAcceptanceEvidenceWave10[] evidence) : IBenefitAcceptanceEvidenceProviderWave10
{
    private readonly IReadOnlyDictionary<string, BenefitAcceptanceEvidenceWave10> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitAcceptanceEvidenceWave10?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record ExecutionCommandRequestWave10(
    ExecutionCommandWave10 Command,
    AuthorityActor Actor,
    ExecutionThreadEnvelopeWave10 Before,
    CommandPolicy Policy,
    ExecutionOwnershipEvidenceWave10 Ownership,
    string Fingerprint);

public sealed record ExecutionCommitWave10(
    ExecutionThreadEnvelopeWave10 After,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public interface IExecutionWave10Store
{
    ValueTask<ExecutionThreadEnvelopeWave10?> GetExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyRecord?> GetExecutionIdempotencyAsync(
        string commandName,
        string executionId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitExecutionCommandAsync(
        ExecutionCommandRequestWave10 request,
        ExecutionCommitWave10 commit,
        CancellationToken cancellationToken = default);
}
