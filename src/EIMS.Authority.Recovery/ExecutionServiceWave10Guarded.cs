namespace EIMS.Authority.Recovery;

/// <summary>
/// Production composition facade for Wave 10 Execution commands.
/// It hardens the inner lifecycle service by requiring the actor's authoritative P3 scope
/// to contain the server-resolved Execution ownership scope even when the client omits requestedScope.
/// P5 and later compositions must bind this facade rather than the inner service directly.
/// </summary>
public sealed class ExecutionServiceWave10Guarded(
    ExecutionServiceWave10 inner,
    IExecutionOwnershipEvidenceProviderWave10 ownershipProvider)
{
    public async ValueTask<AuthorityResult> ExecuteAsync(
        ExecutionCommandWave10 command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
            return AuthorityResult.Deny(401, "P1_IDENTITY_ASSIGNMENT_REQUIRED", command.CorrelationId ?? string.Empty);

        var executionId = command.ExecutionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executionId))
            return AuthorityResult.Deny(400, "P1_EXECUTION_COMMAND_INVALID", command.CorrelationId ?? string.Empty);

        var ownership = await ownershipProvider.ResolveAsync(executionId, cancellationToken);
        if (ownership is null
            || string.IsNullOrWhiteSpace(ownership.Scope))
            return AuthorityResult.Deny(409, "P1_EXECUTION_OWNERSHIP_EVIDENCE_REQUIRED", command.CorrelationId ?? string.Empty);

        if (!actor.Scopes.Any(scope => string.Equals(scope, ownership.Scope, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "P1_EXECUTION_SCOPE_CONTEXT_MISMATCH", command.CorrelationId ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !string.Equals(command.RequestedScope.Trim(), ownership.Scope, StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_EXECUTION_SCOPE_CONTEXT_MISMATCH", command.CorrelationId ?? string.Empty);

        return await inner.ExecuteAsync(command, actor, cancellationToken);
    }
}
