namespace EIMS.Authority.Recovery;

public sealed class StaticBenefitMeasurementAuthorityProviderWave11(
    params BenefitMeasurementAuthorityEvidenceWave11[] evidence) : IBenefitMeasurementAuthorityProviderWave11
{
    private readonly IReadOnlyCollection<BenefitMeasurementAuthorityEvidenceWave11> _evidence = Array.AsReadOnly(evidence);

    public ValueTask<BenefitMeasurementAuthorityEvidenceWave11?> ResolveAsync(
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
