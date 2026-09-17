namespace EIMS.Authority.Recovery;

public sealed record KnowledgeCommandWave13(
    string CommandName,
    string? KnowledgeId,
    string? BenefitId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? RequestedScope = null,
    string? Decision = null,
    string? Note = null,
    string? PublicationDossierRef = null);

public sealed record KnowledgeEnvelopeWave13(
    string KnowledgeId,
    string BenefitId,
    string ExecutionId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    string AuthorPersonId,
    string AuthorAssignmentId,
    string AuthorPolicy,
    string Scope,
    string? ValidatedByPersonId,
    string? ValidatedByAssignmentId,
    string? PublishedByPersonId,
    string? PublishedByAssignmentId,
    string? PublicationDossierRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CorrelationId);

public sealed record KnowledgeBenefitSourceEvidenceWave13(
    string BenefitId,
    string ExecutionId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string BenefitState,
    string VerificationDossierRef,
    string AttributionDossierRef,
    string RealizationDossierRef,
    string Scope,
    string EvidenceRef,
    string EvidenceVersion);

public interface IKnowledgeBenefitSourceProviderWave13
{
    ValueTask<KnowledgeBenefitSourceEvidenceWave13?> ResolveAsync(string benefitId, CancellationToken cancellationToken = default);
}

public sealed class StaticKnowledgeBenefitSourceProviderWave13(params KnowledgeBenefitSourceEvidenceWave13[] evidence)
    : IKnowledgeBenefitSourceProviderWave13
{
    private readonly IReadOnlyDictionary<string, KnowledgeBenefitSourceEvidenceWave13> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<KnowledgeBenefitSourceEvidenceWave13?> ResolveAsync(string benefitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record KnowledgeAuthorAuthorityEvidenceWave13(
    string BenefitId,
    string PersonId,
    string AssignmentId,
    string Scope,
    string ResolvedPolicy,
    bool Authorized,
    string EvidenceRef,
    string EvidenceVersion);

public interface IKnowledgeAuthorPolicyProviderWave13
{
    ValueTask<KnowledgeAuthorAuthorityEvidenceWave13?> ResolveAsync(
        string benefitId,
        AuthorityActor actor,
        CancellationToken cancellationToken = default);
}

public sealed class StaticKnowledgeAuthorPolicyProviderWave13(params KnowledgeAuthorAuthorityEvidenceWave13[] evidence)
    : IKnowledgeAuthorPolicyProviderWave13
{
    private readonly IReadOnlyCollection<KnowledgeAuthorAuthorityEvidenceWave13> _evidence = Array.AsReadOnly(evidence);

    public ValueTask<KnowledgeAuthorAuthorityEvidenceWave13?> ResolveAsync(
        string benefitId,
        AuthorityActor actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = _evidence.FirstOrDefault(x =>
            string.Equals(x.BenefitId, benefitId, StringComparison.Ordinal)
            && string.Equals(x.PersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.AssignmentId, actor.AssignmentId, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(value);
    }
}

public sealed record KnowledgeCommandRequestWave13(
    KnowledgeCommandWave13 Command,
    AuthorityActor Actor,
    KnowledgeEnvelopeWave13? Before,
    CommandPolicy Policy,
    string Fingerprint);

public sealed record KnowledgeCommitWave13(
    KnowledgeEnvelopeWave13 After,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public interface IKnowledgeWave13Store
{
    ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeAsync(string knowledgeId, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEnvelopeWave13?> GetKnowledgeByBenefitAsync(string benefitId, CancellationToken cancellationToken = default);
    ValueTask<IdempotencyRecord?> GetKnowledgeIdempotencyAsync(
        string commandName,
        string aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitKnowledgeCreateAsync(
        KnowledgeCommandRequestWave13 request,
        KnowledgeCommitWave13 commit,
        CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitKnowledgeCommandAsync(
        KnowledgeCommandRequestWave13 request,
        KnowledgeCommitWave13 commit,
        CancellationToken cancellationToken = default);
}
