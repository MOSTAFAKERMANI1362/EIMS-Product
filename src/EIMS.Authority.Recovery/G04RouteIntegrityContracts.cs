namespace EIMS.Authority.Recovery;

public sealed record G04DecisionRouteMetadata(
    string DecisionRoute,
    string DecisionRouteKind,
    string DecisionMethod,
    string GovernanceProfileId,
    string GovernanceProfileVersion)
{
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DecisionRoute)
                || string.IsNullOrWhiteSpace(DecisionRouteKind)
                || string.IsNullOrWhiteSpace(DecisionMethod)
                || string.IsNullOrWhiteSpace(GovernanceProfileId)
                || string.IsNullOrWhiteSpace(GovernanceProfileVersion))
                return false;

            var kind = DecisionRouteKind.Trim().ToUpperInvariant();
            var route = DecisionRoute.Trim().ToUpperInvariant();
            var method = DecisionMethod.Trim().ToUpperInvariant();

            if (kind == "COMMITTEE")
                return route == "G04_COMMITTEE"
                    && method is "MAJORITY" or "CONSENSUS" or "CHAIR_TIEBREAK";

            return kind == "INDIVIDUAL"
                && route != "G04_COMMITTEE"
                && method == "INDIVIDUAL_GOVERNANCE_DECISION";
        }
    }
}

public interface IG04DecisionRouteProvider
{
    ValueTask<G04DecisionRouteMetadata?> ResolveAsync(
        AggregateSnapshot idea,
        CancellationToken cancellationToken = default);
}

public sealed class StaticG04DecisionRouteProvider(G04DecisionRouteMetadata metadata) : IG04DecisionRouteProvider
{
    public ValueTask<G04DecisionRouteMetadata?> ResolveAsync(
        AggregateSnapshot idea,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<G04DecisionRouteMetadata?>(metadata);
    }
}

public static class G04RouteIntegrity
{
    public static bool HasCompleteRoute(EvaluationPlanEnvelope plan) =>
        HasCompleteRoute(plan.DecisionRoute, plan.DecisionRouteKind, plan.DecisionMethod,
            plan.GovernanceProfileId, plan.GovernanceProfileVersion);

    public static bool HasCompleteRoute(G04AssessmentEnvelope assessment) =>
        HasCompleteRoute(assessment.DecisionRoute, assessment.DecisionRouteKind, assessment.DecisionMethod,
            assessment.GovernanceProfileId, assessment.GovernanceProfileVersion);

    public static bool PlanAssessmentMatch(EvaluationPlanEnvelope plan, G04AssessmentEnvelope assessment) =>
        string.Equals(plan.PlanId, assessment.PlanId, StringComparison.Ordinal)
        && string.Equals(plan.IdeaId, assessment.IdeaId, StringComparison.Ordinal)
        && plan.IdeaVersion == assessment.IdeaVersion
        && string.Equals(plan.DecisionRoute, assessment.DecisionRoute, StringComparison.Ordinal)
        && string.Equals(plan.DecisionRouteKind, assessment.DecisionRouteKind, StringComparison.Ordinal)
        && string.Equals(plan.DecisionMethod, assessment.DecisionMethod, StringComparison.Ordinal)
        && string.Equals(plan.GovernanceProfileId, assessment.GovernanceProfileId, StringComparison.Ordinal)
        && string.Equals(plan.GovernanceProfileVersion, assessment.GovernanceProfileVersion, StringComparison.Ordinal);

    public static bool IsCommitteeRoute(G04AssessmentEnvelope assessment) =>
        string.Equals(assessment.DecisionRoute, "G04_COMMITTEE", StringComparison.Ordinal)
        && string.Equals(assessment.DecisionRouteKind, "COMMITTEE", StringComparison.Ordinal)
        && assessment.DecisionMethod is "MAJORITY" or "CONSENSUS" or "CHAIR_TIEBREAK";

    private static bool HasCompleteRoute(
        string? route,
        string? kind,
        string? method,
        string? governanceProfileId,
        string? governanceProfileVersion)
    {
        if (string.IsNullOrWhiteSpace(route)
            || string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(method)
            || string.IsNullOrWhiteSpace(governanceProfileId)
            || string.IsNullOrWhiteSpace(governanceProfileVersion))
            return false;

        return new G04DecisionRouteMetadata(route, kind, method, governanceProfileId, governanceProfileVersion).IsValid;
    }
}
