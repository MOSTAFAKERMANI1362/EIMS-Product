namespace EIMS.Authority.Recovery;

public sealed record BenefitCommandWave11(
    string CommandName,
    string BenefitId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? RequestedScope = null,
    string? BaselineValue = null,
    string? TargetValue = null,
    string? MetricUnit = null,
    string? MeasuredValue = null,
    string? MeasurementEvidenceRef = null,
    string? VerificationDossierRef = null,
    string? AttributionDossierRef = null,
    string? RealizationDossierRef = null);

public sealed record BenefitEnvelopeWave11(
    string BenefitId,
    string ExecutionId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    string? BaselineValue,
    string? TargetValue,
    string? MetricUnit,
    bool MeasurementPlanApproved,
    string? MeasuredValue,
    string? MeasurementEvidenceRef,
    string? VerificationDossierRef,
    string? VerifiedByPersonId,
    string? AttributionDossierRef,
    string? RealizationDossierRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CorrelationId);

public sealed record BenefitAuthorityEvidenceWave11(
    string BenefitId,
    string OwnerPersonId,
    string OwnerAssignmentId,
    string ExecutionOwnerPersonId,
    string Scope,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitAuthorityEvidenceProviderWave11
{
    ValueTask<BenefitAuthorityEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitAuthorityEvidenceProviderWave11(
    params BenefitAuthorityEvidenceWave11[] evidence) : IBenefitAuthorityEvidenceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitAuthorityEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitAuthorityEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record BenefitKnowledgeClosureEvidenceWave11(
    string BenefitId,
    string KnowledgeId,
    bool KnowledgeExists,
    string KnowledgeStatus,
    string? PublicationDossierRef,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitKnowledgeClosureEvidenceProviderWave11
{
    ValueTask<BenefitKnowledgeClosureEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitKnowledgeClosureEvidenceProviderWave11(
    params BenefitKnowledgeClosureEvidenceWave11[] evidence) : IBenefitKnowledgeClosureEvidenceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitKnowledgeClosureEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitKnowledgeClosureEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record BenefitCommandRequestWave11(
    BenefitCommandWave11 Command,
    AuthorityActor Actor,
    BenefitEnvelopeWave11 Before,
    CommandPolicy Policy,
    BenefitAuthorityEvidenceWave11 AuthorityEvidence,
    string Fingerprint);

public sealed record BenefitCommitWave11(
    BenefitEnvelopeWave11 After,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public interface IBenefitWave11Store
{
    ValueTask<BenefitEnvelopeWave11?> GetBenefitAsync(
        string benefitId,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyRecord?> GetBenefitIdempotencyAsync(
        string commandName,
        string benefitId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitBenefitCommandAsync(
        BenefitCommandRequestWave11 request,
        BenefitCommitWave11 commit,
        CancellationToken cancellationToken = default);
}

public sealed record BenefitObligationIntakeRequestWave11(
    OutboxEnvelope SourceEvent,
    string Fingerprint);

public sealed record BenefitObligationIntakeCommitWave11(
    BenefitEnvelopeWave11 Benefit,
    AuditEnvelope Audit);

public interface IBenefitObligationIntakeStoreWave11
{
    ValueTask<IdempotencyRecord?> GetBenefitIntakeIdempotencyAsync(
        string benefitId,
        string sourceMessageId,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitBenefitIntakeAsync(
        BenefitObligationIntakeRequestWave11 request,
        BenefitObligationIntakeCommitWave11 commit,
        CancellationToken cancellationToken = default);
}
