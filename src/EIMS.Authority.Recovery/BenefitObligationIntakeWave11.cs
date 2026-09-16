using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

/// <summary>
/// System-only Wave11 intake. It materializes the authoritative BenefitObligationCreated.v1 handoff from Wave10.
/// No user identity, role or client payload can invoke this path.
/// </summary>
public sealed class BenefitObligationIntakeWave11(IBenefitWave11Store store)
{
    public ValueTask<AuthorityResult> MaterializeAsync(
        BenefitObligationSourceEvidenceWave11 source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(source.EventName, "BenefitObligationCreated.v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(source.BenefitId)
            || string.IsNullOrWhiteSpace(source.ExecutionId)
            || string.IsNullOrWhiteSpace(source.IdeaId)
            || source.ApprovedIdeaVersion <= 0
            || !string.Equals(source.State, "OBLIGATION_PENDING_ACCEPTANCE", StringComparison.Ordinal)
            || source.Version != 1
            || string.IsNullOrWhiteSpace(source.CorrelationId)
            || string.IsNullOrWhiteSpace(source.EvidenceRef)
            || string.IsNullOrWhiteSpace(source.EvidenceVersion))
            return ValueTask.FromResult(AuthorityResult.Deny(
                400,
                "P1_BENEFIT_INTAKE_SOURCE_INVALID",
                source.CorrelationId ?? string.Empty));

        return store.MaterializeBenefitObligationAsync(source, Fingerprint(source), cancellationToken);
    }

    private static string Fingerprint(BenefitObligationSourceEvidenceWave11 source)
    {
        var raw = string.Join("\u001f", new[]
        {
            source.EventName,
            source.BenefitId,
            source.ExecutionId,
            source.IdeaId,
            source.ApprovedIdeaVersion.ToString(),
            source.State,
            source.Version.ToString(),
            source.CorrelationId,
            source.EvidenceRef,
            source.EvidenceVersion
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
