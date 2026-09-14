using System.Text.Json;

namespace EIMS.Authority.Recovery;

public sealed class RecoveredG03MutationPlanner : ICommandMutationPlanner
{
    public ValueTask<MutationPlan?> PlanAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return command.CommandName switch
        {
            "needs.submit-g03" => ValueTask.FromResult<MutationPlan?>(PlanSubmit(aggregate, policy)),
            "needs.g03-decision" => ValueTask.FromResult<MutationPlan?>(PlanDecision(command, aggregate, policy)),
            _ => ValueTask.FromResult<MutationPlan?>(null)
        };
    }

    private static MutationPlan? PlanSubmit(AggregateSnapshot aggregate, CommandPolicy policy)
    {
        var eventName = policy.ResolveEventName();
        if (string.IsNullOrWhiteSpace(eventName))
            return null;

        var facts = CopyFacts(aggregate.RuleFacts);
        facts["g03ReviewStatus"] = "PENDING";

        var after = aggregate with
        {
            State = "PENDING_G03_REVIEW",
            Version = aggregate.Version + 1,
            RuleFacts = facts,
            WorkRoutingRole = "NEED_REVIEWER"
        };

        return new MutationPlan(after, eventName);
    }

    private static MutationPlan? PlanDecision(
        AuthorityCommand command,
        AggregateSnapshot aggregate,
        CommandPolicy policy)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(command.RawBody);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var outcome = ReadString(root, "decision").Trim().ToUpperInvariant();
            if (outcome is not ("APPROVE" or "RETURN"))
                return null;

            var eventName = policy.ResolveEventName(outcome);
            if (string.IsNullOrWhiteSpace(eventName))
                return null;

            var facts = CopyFacts(aggregate.RuleFacts);
            var decisionFacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string state;
            string reviewStatus;
            string routingRole;
            string? note = null;

            if (outcome == "APPROVE")
            {
                state = "READY_FOR_IDEATION";
                reviewStatus = "APPROVED";
                routingRole = "IDEA_OWNER";
                decisionFacts["definitionComplete"] = ReadString(root, "definitionComplete").Trim().ToUpperInvariant();
                decisionFacts["measurable"] = ReadString(root, "measurable").Trim().ToUpperInvariant();
                decisionFacts["solutionBiasFree"] = ReadString(root, "solutionBiasFree").Trim().ToUpperInvariant();
            }
            else
            {
                state = "DRAFT";
                reviewStatus = "RETURNED";
                routingRole = "NEED_OWNER";
                note = ReadString(root, "note").Trim();
            }

            facts["g03ReviewStatus"] = reviewStatus;

            var after = aggregate with
            {
                State = state,
                Version = aggregate.Version + 1,
                RuleFacts = facts,
                WorkRoutingRole = routingRole
            };

            var intent = new DecisionIntent(
                "G03ReviewDecision",
                outcome,
                note,
                decisionFacts.Count == 0 ? null : decisionFacts);

            return new MutationPlan(after, eventName, new[] { intent });
        }
    }

    private static Dictionary<string, string> CopyFacts(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
            return copy;

        foreach (var pair in source)
            copy[pair.Key] = pair.Value;
        return copy;
    }

    private static string ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        return value.GetString() ?? string.Empty;
    }
}
