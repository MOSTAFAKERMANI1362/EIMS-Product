using System.Globalization;

namespace EIMS.Authority.Recovery;

public sealed class RecoveredWave5RuleEvaluator : IRuleEvaluator
{
    private readonly RecoveredG03RuleEvaluator _g03 = new();

    public ValueTask<RuleEvaluation> EvaluateAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (command.CommandName is "needs.submit-g03" or "needs.g03-decision")
            return _g03.EvaluateAsync(command, actor, aggregate, policy, cancellationToken);

        if (string.Equals(command.CommandName, "ideas.submit-g04", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(EvaluateIdeaSubmission(aggregate, policy));

        return ValueTask.FromResult(RuleEvaluation.Fail(
            "P1_RULESET_NOT_BOUND",
            "No recovered Wave 5 rule evaluator is bound for this Product command."));
    }

    private static RuleEvaluation EvaluateIdeaSubmission(AggregateSnapshot aggregate, CommandPolicy policy)
    {
        if (!string.Equals(policy.RuleSet, "P1-IDEA-SUBMIT-G04-REBASELINE-1.0", StringComparison.Ordinal))
            return RuleEvaluation.Fail("IDEA_SUBMIT_RULESET_MISMATCH");

        var facts = aggregate.RuleFacts;
        if (facts is null)
            return RuleEvaluation.Fail("IDEA_SUBMIT_FACTS_REQUIRED", "Authoritative Idea submission facts are missing from persistence.");

        if (!TryFact(facts, "passportCompletionPercent", out var passportRaw)
            || !decimal.TryParse(passportRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var passport))
            return RuleEvaluation.Fail("IDEA_SUBMIT_PASSPORT_REQUIRED");

        if (passport < 70m)
            return RuleEvaluation.Fail("IDEA_SUBMIT_PASSPORT_BELOW_70");

        if (!TryRequiredTrue(facts, "strategyLinkActive", out var strategyCode))
            return RuleEvaluation.Fail(strategyCode);

        if (!TryRequiredTrue(facts, "primaryObjectiveReady", out var objectiveCode))
            return RuleEvaluation.Fail(objectiveCode);

        foreach (var key in SpecialistFlagKeys)
        {
            if (!TryBooleanFact(facts, key, out _))
                return RuleEvaluation.Fail("IDEA_SUBMIT_PROFILE_FACT_INVALID", $"Authoritative profile flag '{key}' is missing or invalid.");
        }

        return RuleEvaluation.Pass("IDEA_SUBMIT_RULES_PASS");
    }

    internal static readonly string[] SpecialistFlagKeys =
    {
        "requiresTechnicalEvaluation",
        "requiresHseEvaluation",
        "requiresFinancialEvaluation",
        "requiresItEvaluation"
    };

    internal static bool TryBooleanFact(IReadOnlyDictionary<string, string> facts, string key, out bool value)
    {
        if (!TryFact(facts, key, out var raw))
        {
            value = false;
            return false;
        }

        switch (raw.Trim().ToUpperInvariant())
        {
            case "YES":
            case "TRUE":
            case "1":
            case "ACTIVE":
            case "READY":
                value = true;
                return true;
            case "NO":
            case "FALSE":
            case "0":
            case "INACTIVE":
            case "NOT_READY":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryRequiredTrue(
        IReadOnlyDictionary<string, string> facts,
        string key,
        out string failureCode)
    {
        if (!TryBooleanFact(facts, key, out var value))
        {
            failureCode = key == "strategyLinkActive"
                ? "IDEA_SUBMIT_STRATEGY_LINK_REQUIRED"
                : "IDEA_SUBMIT_PRIMARY_OBJECTIVE_REQUIRED";
            return false;
        }

        if (!value)
        {
            failureCode = key == "strategyLinkActive"
                ? "IDEA_SUBMIT_STRATEGY_LINK_INACTIVE"
                : "IDEA_SUBMIT_PRIMARY_OBJECTIVE_NOT_READY";
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    private static bool TryFact(IReadOnlyDictionary<string, string> facts, string key, out string value)
    {
        if (facts.TryGetValue(key, out var exact) && exact is not null)
        {
            value = exact;
            return true;
        }

        foreach (var pair in facts)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
