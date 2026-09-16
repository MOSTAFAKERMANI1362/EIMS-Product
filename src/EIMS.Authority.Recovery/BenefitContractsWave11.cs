namespace EIMS.Authority.Recovery;

public sealed record BenefitCommandWave11(
    string CommandName,
    string BenefitId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? RequestedScope = null,
    string? BaselineEvidenceRef = null,
    string? TargetEvidenceRef = null,
    string? MeasurementPlanRef = null,
    string? MeasurementDossierRef = null,
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
    string? BaselineEvidenceRef,
    string? TargetEvidenceRef,
    string? MeasurementPlanRef,
    string? MeasurementDossierRef,
    string? VerificationDossierRef,
    string? AttributionDossierRef,
    string? RealizationDossierRef,
    string? AcceptedByPersonId,
    string? AcceptedByAssignmentId,
    string? MeasuredByPersonId,
    string? VerifiedByPersonId,
    string? KnowledgeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CorrelationId);

public sealed record BenefitOwnershipEvidenceWave11(
    string BenefitId,
    string OwnerPersonId,
    string OwnerAssignmentId,
    string Scope,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitOwnershipEvidenceProviderWave11
{
    ValueTask<BenefitOwnershipEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitOwnershipEvidenceProviderWave11(
    params BenefitOwnershipEvidenceWave11[] evidence) : IBenefitOwnershipEvidenceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitOwnershipEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitOwnershipEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record BenefitBaselineTargetEvidenceWave11(
    string BenefitId,
    string BaselineEvidenceRef,
    string TargetEvidenceRef,
    bool BaselineValid,
    bool TargetValid,
    bool Distinct,
    string BenefitClass,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitBaselineTargetEvidenceProviderWave11
{
    ValueTask<BenefitBaselineTargetEvidenceWave11?> ValidateAsync(
        string benefitId,
        string baselineEvidenceRef,
        string targetEvidenceRef,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitBaselineTargetEvidenceProviderWave11(
    params BenefitBaselineTargetEvidenceWave11[] evidence) : IBenefitBaselineTargetEvidenceProviderWave11
{
    private readonly IReadOnlyCollection<BenefitBaselineTargetEvidenceWave11> _evidence = Array.AsReadOnly(evidence);

    public ValueTask<BenefitBaselineTargetEvidenceWave11?> ValidateAsync(
        string benefitId,
        string baselineEvidenceRef,
        string targetEvidenceRef,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = _evidence.FirstOrDefault(x =>
            string.Equals(x.BenefitId, benefitId, StringComparison.Ordinal)
            && string.Equals(x.BaselineEvidenceRef, baselineEvidenceRef, StringComparison.Ordinal)
            && string.Equals(x.TargetEvidenceRef, targetEvidenceRef, StringComparison.Ordinal));
        return ValueTask.FromResult(value);
    }
}

public sealed record BenefitExecutionOwnerEvidenceWave11(
    string BenefitId,
    string ExecutionId,
    string ExecutionOwnerPersonId,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitExecutionOwnerEvidenceProviderWave11
{
    ValueTask<BenefitExecutionOwnerEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitExecutionOwnerEvidenceProviderWave11(
    params BenefitExecutionOwnerEvidenceWave11[] evidence) : IBenefitExecutionOwnerEvidenceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitExecutionOwnerEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitExecutionOwnerEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record BenefitMeasurementAuthorityEvidenceWave11(
    string BenefitId,
    string PersonId,
    string AssignmentId,
    string Scope,
    bool Authorized,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitMeasurementAuthorityProviderWave11
{
    ValueTask<BenefitMeasurementAuthorityEvidenceWave11?> ResolveAsync(
        string benefitId,
        AuthorityActor actor,
        CancellationToken cancellationToken = default);
}

public sealed class BenefitOwnerOnlyMeasurementAuthorityProviderWave11 : IBenefitMeasurementAuthorityProviderWave11
{
    public ValueTask<BenefitMeasurementAuthorityEvidenceWave11?> ResolveAsync(
        string benefitId,
        AuthorityActor actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<BenefitMeasurementAuthorityEvidenceWave11?>(null);
    }
}

public sealed record BenefitKnowledgePublicationEvidenceWave11(
    string BenefitId,
    string KnowledgeId,
    bool Exists,
    string Status,
    string PublicationDossierRef,
    string EvidenceRef,
    string EvidenceVersion);

public interface IBenefitKnowledgePublicationEvidenceProviderWave11
{
    ValueTask<BenefitKnowledgePublicationEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitKnowledgePublicationEvidenceProviderWave11(
    params BenefitKnowledgePublicationEvidenceWave11[] evidence) : IBenefitKnowledgePublicationEvidenceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitKnowledgePublicationEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitKnowledgePublicationEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

public sealed record BenefitObligationSourceEvidenceWave11(
    string EventName,
    string BenefitId,
    string ExecutionId,
    string IdeaId,
    long ApprovedIdeaVersion,
    string State,
    long Version,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string EvidenceRef,
    string EvidenceVersion);

public sealed record BenefitCommandRequestWave11(
    BenefitCommandWave11 Command,
    AuthorityActor Actor,
    BenefitEnvelopeWave11 Before,
    CommandPolicy Policy,
    BenefitOwnershipEvidenceWave11 Ownership,
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

    ValueTask<AuthorityResult> MaterializeBenefitObligationAsync(
        BenefitObligationSourceEvidenceWave11 source,
        string fingerprint,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityResult> CommitBenefitCommandAsync(
        BenefitCommandRequestWave11 request,
        BenefitCommitWave11 commit,
        CancellationToken cancellationToken = default);
}
