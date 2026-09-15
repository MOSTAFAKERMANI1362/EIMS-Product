namespace EIMS.Authority.Recovery;

public sealed class RecoveredWave5MutationPlanner : ICommandMutationPlanner
{
    private readonly RecoveredG03MutationPlanner _g03 = new();

    public ValueTask<MutationPlan?> PlanAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (command.CommandName is "needs.submit-g03" or "needs.g03-decision")
            return _g03.PlanAsync(command, actor, aggregate, policy, cancellationToken);

        if (string.Equals(command.CommandName, "ideas.submit-g04", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<MutationPlan?>(PlanIdeaSubmission(aggregate, policy));

        return ValueTask.FromResult<MutationPlan?>(null);
    }

    private static MutationPlan? PlanIdeaSubmission(AggregateSnapshot aggregate, CommandPolicy policy)
    {
        var eventName = policy.ResolveEventName();
        if (!string.Equals(eventName, "IdeaSubmittedForEvaluation.v1", StringComparison.Ordinal))
            return null;

        if (string.IsNullOrWhiteSpace(aggregate.Scope) || aggregate.RuleFacts is null)
            return null;

        var scope = aggregate.Scope.Trim();
        var facts = CopyFacts(aggregate.RuleFacts);
        facts["g04SubmissionStatus"] = "UNDER_REVIEW";
        facts["evaluationPlanStatus"] = "ACTIVE";

        var assignments = new List<EvaluationAssignmentIntent>
        {
            new("IDEA_EVALUATOR", scope, Required: true),
            new("UNIT_OWNER_REVIEWER", scope, Required: true)
        };

        AddSpecialist(assignments, aggregate.RuleFacts, "requiresTechnicalEvaluation", "TECHNICAL_ASSESSOR", scope);
        AddSpecialist(assignments, aggregate.RuleFacts, "requiresHseEvaluation", "HSE_ASSESSOR", scope);
        AddSpecialist(assignments, aggregate.RuleFacts, "requiresFinancialEvaluation", "FINANCIAL_ASSESSOR", scope);
        AddSpecialist(assignments, aggregate.RuleFacts, "requiresItEvaluation", "IT_ASSESSOR", scope);

        var after = aggregate with
        {
            State = "UNDER_REVIEW",
            Version = aggregate.Version + 1,
            RuleFacts = facts,
            WorkRoutingRole = null
        };

        return new MutationPlan(
            after,
            eventName,
            DecisionIntents: null,
            EvaluationPlanIntent: new EvaluationPlanIntent("ACTIVE", 1, assignments.AsReadOnly()),
            ServerTimestampFactKeys: new[] { "g04SubmittedAtUtc" });
    }

    private static void AddSpecialist(
        ICollection<EvaluationAssignmentIntent> assignments,
        IReadOnlyDictionary<string, string> facts,
        string flag,
        string role,
        string scope)
    {
        if (RecoveredWave5RuleEvaluator.TryBooleanFact(facts, flag, out var required) && required)
            assignments.Add(new EvaluationAssignmentIntent(role, scope, Required: true));
    }

    private static Dictionary<string, string> CopyFacts(IReadOnlyDictionary<string, string> source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
            copy[pair.Key] = pair.Value;
        return copy;
    }
}
