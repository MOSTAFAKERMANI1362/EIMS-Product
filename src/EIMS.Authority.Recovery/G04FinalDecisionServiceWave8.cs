namespace EIMS.Authority.Recovery;

/// <summary>
/// Production composition façade for Wave 8 G04 final decision.
/// It resolves an exact idempotent replay before reading mutable Idea/Plan/Assessment state,
/// so RETURN remains replay-safe after it increments the business Idea revision and supersedes the Plan.
/// P3 must still resolve the current authenticated AuthorityActor before this service is invoked.
/// </summary>
public sealed class G04FinalDecisionServiceWave8(
    IG04FinalDecisionStore store,
    IG04FinalDecisionEvidenceProvider evidenceProvider,
    ICommandPolicyCatalog? catalog = null)
{
    private readonly G04FinalDecisionService _inner = new(store, evidenceProvider, catalog);

    public async ValueTask<AuthorityResult> DecideAsync(
        G04FinalDecisionCommand command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
            return AuthorityResult.Deny(401, "P1_IDENTITY_ASSIGNMENT_REQUIRED", command.CorrelationId);

        var roles = actor.Roles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roles.Length != 1 || !string.Equals(roles[0], "IDEA_DECISION", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_G04_FINAL_AUTHORITY_REQUIRED", command.CorrelationId);

        if (string.IsNullOrWhiteSpace(command.AssessmentId)
            || string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return await _inner.DecideAsync(command, actor, cancellationToken);

        var normalized = command with
        {
            Outcome = command.Outcome?.Trim().ToUpperInvariant() ?? string.Empty,
            ReasonCode = command.ReasonCode?.Trim().ToUpperInvariant() ?? string.Empty,
            DecisionComment = command.DecisionComment?.Trim() ?? string.Empty
        };
        var fingerprint = G04FinalDecisionService.Fingerprint(normalized, actor);
        var prior = await store.GetFinalDecisionIdempotencyAsync(
            command.AssessmentId, command.IdempotencyKey, cancellationToken);

        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId);

            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = command.CorrelationId
            };
        }

        return await _inner.DecideAsync(normalized, actor, cancellationToken);
    }
}
