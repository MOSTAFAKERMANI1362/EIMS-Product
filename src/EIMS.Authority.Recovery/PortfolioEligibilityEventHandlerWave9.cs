namespace EIMS.Authority.Recovery;

public sealed record PortfolioEligibilityFactsWave9(
    int HardReadinessGaps,
    bool PolicyExcluded);

public interface IPortfolioEligibilityFactsProviderWave9
{
    ValueTask<PortfolioEligibilityFactsWave9?> GetFactsAsync(
        string ideaId,
        long approvedIdeaVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// System-only outbox consumer for the committed G04 APPROVE event.
/// Readiness/policy facts are loaded from a server-side provider and are never trusted from event/client payload fields.
/// </summary>
public sealed class PortfolioEligibilityEventHandlerWave9(
    PortfolioServiceWave9 service,
    IPortfolioEligibilityFactsProviderWave9 factsProvider)
{
    public async ValueTask<AuthorityResult> HandleAsync(
        OutboxEnvelope message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(message.EventName, "IdeaApprovedForPortfolio.v1", StringComparison.Ordinal))
            return AuthorityResult.Deny(400, "P1_PORTFOLIO_EVENT_NOT_ELIGIBILITY_TRIGGER", message.CorrelationId);
        if (message.Payload is null
            || !message.Payload.TryGetValue("ideaId", out var ideaId)
            || string.IsNullOrWhiteSpace(ideaId)
            || !message.Payload.TryGetValue("ideaRevision", out var revisionText)
            || !long.TryParse(revisionText, out var approvedIdeaVersion)
            || approvedIdeaVersion <= 0
            || !message.Payload.TryGetValue("outcome", out var outcome)
            || !string.Equals(outcome, "APPROVE", StringComparison.OrdinalIgnoreCase)
            || !message.Payload.TryGetValue("ideaState", out var ideaState)
            || !string.Equals(ideaState, "APPROVED", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(400, "P1_PORTFOLIO_APPROVAL_EVENT_PAYLOAD_INVALID", message.CorrelationId);

        var facts = await factsProvider.GetFactsAsync(ideaId.Trim(), approvedIdeaVersion, cancellationToken);
        if (facts is null)
            return AuthorityResult.Deny(409, "P1_PORTFOLIO_ELIGIBILITY_FACTS_REQUIRED", message.CorrelationId);

        return await service.EvaluateEligibilityAsync(
            new PortfolioEligibilityTrigger(
                message.EventName,
                ideaId.Trim(),
                approvedIdeaVersion,
                "APPROVED",
                facts.HardReadinessGaps,
                facts.PolicyExcluded,
                message.CorrelationId),
            cancellationToken);
    }
}
