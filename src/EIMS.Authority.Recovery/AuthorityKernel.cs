using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

public sealed record MutationPlan(AggregateSnapshot After, string EventName);

public interface ICommandMutationPlanner
{
    ValueTask<MutationPlan?> PlanAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default);
}

public sealed class AuthorityKernel(
    ICommandPolicyCatalog catalog,
    IAuthorityStore store,
    IRuleEvaluator rules,
    ISodEvaluator sod,
    ICommandMutationPlanner planner)
{
    public async ValueTask<AuthorityResult> ExecuteAsync(
        AuthorityCommand command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
            return AuthorityResult.Deny(401, "P1_IDENTITY_ASSIGNMENT_REQUIRED", command.CorrelationId);

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return AuthorityResult.Deny(400, "P1_IDEMPOTENCY_KEY_REQUIRED", command.CorrelationId);

        if (!catalog.TryGet(command.CommandName, out var policy))
            return AuthorityResult.Deny(404, "P1_COMMAND_UNKNOWN", command.CorrelationId);

        if (!policy.StateContractRecovered)
            return AuthorityResult.Deny(503, "P1_STATE_CONTRACT_NOT_RECOVERED", command.CorrelationId,
                "Recovered API role exists, but the authoritative state contract for this command is not physically available.");

        if (!policy.RuleContractRecovered)
            return AuthorityResult.Deny(503, "P1_RULE_CONTRACT_NOT_RECOVERED", command.CorrelationId);

        var roleAllowed = policy.RequiredRoles.Any(required =>
            actor.Roles.Contains(required, StringComparer.OrdinalIgnoreCase));
        if (!roleAllowed)
            return AuthorityResult.Deny(403, "P1_ROLE_SCOPE_DENIED", command.CorrelationId);

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !actor.Scopes.Contains(command.RequestedScope, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(403, "P1_ROLE_SCOPE_DENIED", command.CorrelationId, "Requested scope is not assigned to actor.");

        var aggregate = await store.GetAggregateAsync(command.AggregateId, cancellationToken);
        if (aggregate is null)
            return AuthorityResult.Deny(404, "P1_AGGREGATE_NOT_FOUND", command.CorrelationId);

        var fingerprint = Fingerprint(command);
        var prior = await store.GetIdempotencyAsync(command.CommandName, command.AggregateId, command.IdempotencyKey, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", command.CorrelationId,
                    "Idempotency key was already used with a different command payload/version.");

            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = command.CorrelationId
            };
        }

        if (command.ExpectedVersion != aggregate.Version)
            return AuthorityResult.Deny(409, "P1_VERSION_CONFLICT", command.CorrelationId,
                $"Expected {command.ExpectedVersion}; current {aggregate.Version}.");

        if (policy.AllowedStates.Count == 0
            || !policy.AllowedStates.Contains(aggregate.State, StringComparer.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_STATE_TRANSITION_DENIED", command.CorrelationId,
                $"State '{aggregate.State}' is not allowed for '{command.CommandName}'.");

        var sodResult = await sod.EvaluateAsync(command, actor, aggregate, policy, cancellationToken);
        if (!sodResult.Passed)
            return AuthorityResult.Deny(403, sodResult.Code, command.CorrelationId, sodResult.Detail);

        var ruleResult = await rules.EvaluateAsync(command, actor, aggregate, policy, cancellationToken);
        if (!ruleResult.Passed)
            return AuthorityResult.Deny(422, ruleResult.Code, command.CorrelationId, ruleResult.Detail);

        var plan = await planner.PlanAsync(command, actor, aggregate, policy, cancellationToken);
        if (plan is null)
            return AuthorityResult.Deny(503, "P1_MUTATION_PLAN_NOT_BOUND", command.CorrelationId);

        if (plan.After.Version != aggregate.Version + 1)
            return AuthorityResult.Deny(500, "P1_MUTATION_VERSION_INVALID", command.CorrelationId);

        if (!string.Equals(plan.After.AggregateId, aggregate.AggregateId, StringComparison.Ordinal))
            return AuthorityResult.Deny(500, "P1_MUTATION_AGGREGATE_MISMATCH", command.CorrelationId);

        var now = DateTimeOffset.UtcNow;
        var audit = new AuditEnvelope(
            $"AUD-{Guid.NewGuid():N}",
            actor.PersonId,
            actor.NetworkIdentity,
            actor.IdentitySource,
            actor.Roles,
            actor.AssignmentId,
            aggregate.AggregateId,
            plan.After.Version,
            policy.RuleSet,
            now,
            command.CorrelationId,
            command.CommandName);
        var outbox = new OutboxEnvelope(
            $"MSG-{Guid.NewGuid():N}",
            plan.EventName,
            aggregate.AggregateId,
            plan.After.Version,
            command.CorrelationId,
            now);

        return await store.CommitAsync(
            new MutationRequest(command, actor, aggregate, policy, fingerprint),
            new MutationCommit(plan.After, audit, outbox),
            cancellationToken);
    }

    public static string Fingerprint(AuthorityCommand command)
    {
        var material = $"{command.CommandName}\n{command.AggregateId}\n{command.ExpectedVersion}\n{command.RawBody}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
