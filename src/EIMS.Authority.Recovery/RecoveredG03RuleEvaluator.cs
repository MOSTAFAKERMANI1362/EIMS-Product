using System.Text.Json;

namespace EIMS.Authority.Recovery;

public sealed class RecoveredG03RuleEvaluator : IRuleEvaluator
{
    public ValueTask<RuleEvaluation> EvaluateAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        return command.CommandName switch
        {
            "needs.submit-g03" => ValueTask.FromResult(EvaluateSubmit(aggregate, policy)),
            "needs.g03-decision" => ValueTask.FromResult(EvaluateDecision(command, aggregate, policy)),
            _ => ValueTask.FromResult(RuleEvaluation.Fail("P1_RULESET_NOT_BOUND", "No recovered rule evaluator is bound for this Product command."))
        };
    }

    private static RuleEvaluation EvaluateSubmit(AggregateSnapshot aggregate, CommandPolicy policy)
    {
        if (!string.Equals(policy.RuleSet, "P1-G03-SUBMIT-REBASELINE-1.0", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_RULESET_MISMATCH");

        var facts = aggregate.RuleFacts;
        if (facts is null)
            return RuleEvaluation.Fail("G03_RULE_FACTS_REQUIRED", "Authoritative Need definition facts were not loaded from persistence.");

        if (!TryFact(facts, "title", out var title)
            || !TryFact(facts, "owner", out var owner)
            || !TryFact(facts, "current", out var current)
            || !TryFact(facts, "desired", out var desired)
            || !TryFact(facts, "gap", out var gap))
            return RuleEvaluation.Fail("G03_RULE_FACTS_REQUIRED", "title, owner, current, desired and gap must come from the authoritative aggregate snapshot.");

        title = title.Trim();
        owner = owner.Trim();
        current = current.Trim();
        desired = desired.Trim();
        gap = gap.Trim();

        if (title.Length < 10)
            return RuleEvaluation.Fail("G03_S01_TITLE_TOO_SHORT");
        if (title.StartsWith("نیاز مرتبط با", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S02_TEMP_TITLE");
        if (owner.Length < 3)
            return RuleEvaluation.Fail("G03_S03_OWNER_TOO_SHORT");
        if (string.Equals(owner, "مالک فرآیند مرتبط", StringComparison.Ordinal)
            || string.Equals(owner, "مالک واحد موضوع", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S04_OWNER_PLACEHOLDER");
        if (current.Length < 15)
            return RuleEvaluation.Fail("G03_S05_CURRENT_TOO_SHORT");
        if (current.Contains("وضعیت موجود نیازمند تکمیل", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S06_CURRENT_PLACEHOLDER");
        if (desired.Length < 15)
            return RuleEvaluation.Fail("G03_S07_DESIRED_TOO_SHORT");
        if (desired.Contains("وضعیت مطلوب را تکمیل کنید", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S08_DESIRED_PLACEHOLDER");
        if (gap.Length < 10)
            return RuleEvaluation.Fail("G03_S09_GAP_TOO_SHORT");
        if (gap.Contains("نیازمند تکمیل", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S10_GAP_PLACEHOLDER");
        if (string.Equals(current, desired, StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_S11_CURRENT_EQUALS_DESIRED");

        return RuleEvaluation.Pass("G03_SUBMIT_RULES_PASS");
    }

    private static RuleEvaluation EvaluateDecision(AuthorityCommand command, AggregateSnapshot aggregate, CommandPolicy policy)
    {
        if (!string.Equals(policy.RuleSet, "P1-G03-DECISION-REBASELINE-1.0", StringComparison.Ordinal))
            return RuleEvaluation.Fail("G03_RULESET_MISMATCH");

        var facts = aggregate.RuleFacts;
        if (facts is null || !TryFact(facts, "g03ReviewStatus", out var reviewStatus))
            return RuleEvaluation.Fail("G03_D01_REVIEW_STATUS_REQUIRED", "Authoritative G03 review status was not loaded from persistence.");

        if (!string.Equals(reviewStatus.Trim(), "PENDING", StringComparison.OrdinalIgnoreCase))
            return RuleEvaluation.Fail("G03_D01_REVIEW_NOT_PENDING");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(command.RawBody);
        }
        catch (JsonException)
        {
            return RuleEvaluation.Fail("G03_DECISION_JSON_INVALID");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return RuleEvaluation.Fail("G03_DECISION_JSON_INVALID");

            var decision = ReadString(root, "decision").Trim().ToUpperInvariant();
            if (decision is not ("APPROVE" or "RETURN"))
                return RuleEvaluation.Fail("G03_D02_DECISION_INVALID");

            if (decision == "APPROVE")
            {
                if (!IsYes(root, "definitionComplete"))
                    return RuleEvaluation.Fail("G03_D03_DEFINITION_NOT_CONFIRMED");
                if (!IsYes(root, "measurable"))
                    return RuleEvaluation.Fail("G03_D03_MEASURABLE_NOT_CONFIRMED");
                if (!IsYes(root, "solutionBiasFree"))
                    return RuleEvaluation.Fail("G03_D03_SOLUTION_BIAS_NOT_CONFIRMED");
                return RuleEvaluation.Pass("G03_DECISION_APPROVE_RULES_PASS");
            }

            var note = ReadString(root, "note").Trim();
            if (note.Length < 10)
                return RuleEvaluation.Fail("G03_D04_RETURN_NOTE_TOO_SHORT");

            return RuleEvaluation.Pass("G03_DECISION_RETURN_RULES_PASS");
        }
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

    private static string ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        return value.GetString() ?? string.Empty;
    }

    private static bool IsYes(JsonElement root, string property) =>
        string.Equals(ReadString(root, property).Trim(), "YES", StringComparison.OrdinalIgnoreCase);
}
