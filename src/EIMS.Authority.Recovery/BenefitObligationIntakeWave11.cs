using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public interface IBenefitObligationSourceProviderWave11
{
    ValueTask<BenefitObligationSourceEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default);
}

public sealed class StaticBenefitObligationSourceProviderWave11(
    params BenefitObligationSourceEvidenceWave11[] evidence) : IBenefitObligationSourceProviderWave11
{
    private readonly IReadOnlyDictionary<string, BenefitObligationSourceEvidenceWave11> _byBenefit =
        evidence.ToDictionary(x => x.BenefitId, StringComparer.Ordinal);

    public ValueTask<BenefitObligationSourceEvidenceWave11?> ResolveAsync(
        string benefitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byBenefit.TryGetValue(benefitId, out var value) ? value : null);
    }
}

/// <summary>
/// Internal compatibility helper retained only for the dedicated Wave11 contract-test assembly.
/// Production composition must bind BenefitObligationEventHandlerWave11 instead.
/// </summary>
internal sealed class BenefitObligationIntakeWave11(IBenefitWave11Store store)
{
    public ValueTask<AuthorityResult> MaterializeAsync(
        BenefitObligationSourceEvidenceWave11 source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ValidSource(source, source.BenefitId, source.CorrelationId))
            return ValueTask.FromResult(AuthorityResult.Deny(
                400,
                "P1_BENEFIT_INTAKE_SOURCE_INVALID",
                source.CorrelationId ?? string.Empty));

        return store.MaterializeBenefitObligationAsync(source, SourceFingerprint(source), cancellationToken);
    }

    private static bool ValidSource(
        BenefitObligationSourceEvidenceWave11 source,
        string benefitId,
        string correlationId) =>
        !string.IsNullOrWhiteSpace(benefitId)
        && !string.IsNullOrWhiteSpace(correlationId)
        && string.Equals(source.EventName, "BenefitObligationCreated.v1", StringComparison.Ordinal)
        && string.Equals(source.BenefitId, benefitId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(source.ExecutionId)
        && !string.IsNullOrWhiteSpace(source.IdeaId)
        && source.ApprovedIdeaVersion > 0
        && string.Equals(source.State, "OBLIGATION_PENDING_ACCEPTANCE", StringComparison.Ordinal)
        && source.Version == 1
        && string.Equals(source.CorrelationId, correlationId, StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(source.EvidenceRef) == false
        && string.IsNullOrWhiteSpace(source.EvidenceVersion) == false;

    private static string SourceFingerprint(BenefitObligationSourceEvidenceWave11 source)
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

/// <summary>
/// System-only consumer for the authoritative Wave10 BenefitObligationCreated.v1 event.
/// The event is only a trigger. Full Benefit thread facts are reloaded from a server-side provider,
/// and an exact replay is resolved before that provider is consulted.
/// </summary>
public sealed class BenefitObligationEventHandlerWave11(
    IBenefitWave11Store store,
    IBenefitObligationSourceProviderWave11 sourceProvider)
{
    private const string IntakeCommand = "system.benefit-obligation-intake";

    public async ValueTask<AuthorityResult> HandleAsync(
        OutboxEnvelope sourceEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(sourceEvent.EventName, "BenefitObligationCreated.v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceEvent.MessageId)
            || string.IsNullOrWhiteSpace(sourceEvent.AggregateId)
            || sourceEvent.AggregateVersion != 1
            || string.IsNullOrWhiteSpace(sourceEvent.CorrelationId))
            return AuthorityResult.Deny(
                400,
                "P1_BENEFIT_INTAKE_EVENT_INVALID",
                sourceEvent.CorrelationId ?? string.Empty);

        var benefitId = sourceEvent.AggregateId.Trim();
        var fingerprint = EventFingerprint(sourceEvent);

        var prior = await store.GetBenefitIdempotencyAsync(
            IntakeCommand,
            benefitId,
            benefitId,
            cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", sourceEvent.CorrelationId);

            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = sourceEvent.CorrelationId
            };
        }

        var source = await sourceProvider.ResolveAsync(benefitId, cancellationToken);
        if (!ValidSource(source, benefitId, sourceEvent.CorrelationId))
            return AuthorityResult.Deny(
                409,
                "P1_BENEFIT_INTAKE_SOURCE_EVIDENCE_REQUIRED",
                sourceEvent.CorrelationId);
        var resolved = source!;

        if (!EventPayloadConsistent(sourceEvent.Payload, resolved))
            return AuthorityResult.Deny(
                409,
                "P1_BENEFIT_INTAKE_EVENT_SOURCE_MISMATCH",
                sourceEvent.CorrelationId);

        return await store.MaterializeBenefitObligationAsync(
            resolved,
            fingerprint,
            cancellationToken);
    }

    private static bool ValidSource(
        BenefitObligationSourceEvidenceWave11? source,
        string benefitId,
        string correlationId) =>
        source is not null
        && string.Equals(source.EventName, "BenefitObligationCreated.v1", StringComparison.Ordinal)
        && string.Equals(source.BenefitId, benefitId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(source.ExecutionId)
        && !string.IsNullOrWhiteSpace(source.IdeaId)
        && source.ApprovedIdeaVersion > 0
        && string.Equals(source.State, "OBLIGATION_PENDING_ACCEPTANCE", StringComparison.Ordinal)
        && source.Version == 1
        && string.Equals(source.CorrelationId, correlationId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(source.EvidenceRef)
        && !string.IsNullOrWhiteSpace(source.EvidenceVersion);

    private static bool EventPayloadConsistent(
        IReadOnlyDictionary<string, string>? payload,
        BenefitObligationSourceEvidenceWave11 source)
    {
        if (payload is null) return true;
        return MatchIfPresent(payload, "benefitId", source.BenefitId)
            && MatchIfPresent(payload, "executionId", source.ExecutionId)
            && MatchIfPresent(payload, "ideaId", source.IdeaId)
            && MatchIfPresent(payload, "approvedIdeaVersion", source.ApprovedIdeaVersion.ToString())
            && MatchIfPresent(payload, "state", source.State);
    }

    private static bool MatchIfPresent(
        IReadOnlyDictionary<string, string> payload,
        string key,
        string expected) =>
        !payload.TryGetValue(key, out var value)
        || string.Equals(value, expected, StringComparison.Ordinal);

    private static string EventFingerprint(OutboxEnvelope sourceEvent)
    {
        var payload = sourceEvent.Payload is null
            ? string.Empty
            : string.Join(";", sourceEvent.Payload
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + "=" + x.Value));

        var raw = string.Join('|', new[]
        {
            sourceEvent.MessageId,
            sourceEvent.EventName,
            sourceEvent.AggregateId,
            sourceEvent.AggregateVersion.ToString(),
            sourceEvent.CorrelationId,
            payload
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
