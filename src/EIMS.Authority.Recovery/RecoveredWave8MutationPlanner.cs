namespace EIMS.Authority.Recovery;

public sealed class RecoveredWave8MutationPlanner(IG04DecisionRouteProvider routeProvider) : ICommandMutationPlanner
{
    private readonly RecoveredWave5MutationPlanner _historical = new();

    public async ValueTask<MutationPlan?> PlanAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var plan = await _historical.PlanAsync(command, actor, aggregate, policy, cancellationToken);
        if (plan is null || !string.Equals(command.CommandName, "ideas.submit-g04", StringComparison.OrdinalIgnoreCase))
            return plan;

        if (plan.EvaluationPlanIntent is null)
            return null;

        var route = await routeProvider.ResolveAsync(aggregate, cancellationToken);
        if (route is null || !route.IsValid)
            return null;

        var normalized = route with
        {
            DecisionRoute = route.DecisionRoute.Trim().ToUpperInvariant(),
            DecisionRouteKind = route.DecisionRouteKind.Trim().ToUpperInvariant(),
            DecisionMethod = route.DecisionMethod.Trim().ToUpperInvariant(),
            GovernanceProfileId = route.GovernanceProfileId.Trim(),
            GovernanceProfileVersion = route.GovernanceProfileVersion.Trim()
        };

        return plan with
        {
            EvaluationPlanIntent = plan.EvaluationPlanIntent with
            {
                DecisionRoute = normalized.DecisionRoute,
                DecisionRouteKind = normalized.DecisionRouteKind,
                DecisionMethod = normalized.DecisionMethod,
                GovernanceProfileId = normalized.GovernanceProfileId,
                GovernanceProfileVersion = normalized.GovernanceProfileVersion
            }
        };
    }
}
