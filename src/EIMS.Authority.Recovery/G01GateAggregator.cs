namespace EIMS.Authority.Recovery;

public static class G01GateAggregator
{
    public static string Aggregate(IReadOnlyCollection<G01RuleExecutionSnapshot> executions)
    {
        if (executions is null || executions.Count != 4)
            return "G01_BLOCKED";

        var outcomes = executions.Select(x => x?.Outcome).ToArray();

        if (outcomes.Any(x => string.IsNullOrWhiteSpace(x)))
            return "G01_BLOCKED";

        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PASS",
            "FAIL",
            "WARNING",
            "NOT_APPLICABLE",
            "ERROR"
        };

        if (outcomes.Any(x => !valid.Contains(x!)))
            return "G01_BLOCKED";

        if (outcomes.Any(x => string.Equals(x, "ERROR", StringComparison.OrdinalIgnoreCase)))
            return "G01_BLOCKED";

        if (outcomes.Any(x => string.Equals(x, "FAIL", StringComparison.OrdinalIgnoreCase)))
            return "G01_INCOMPLETE";

        if (outcomes.Any(x =>
                string.Equals(x, "WARNING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "NOT_APPLICABLE", StringComparison.OrdinalIgnoreCase)))
            return "G01_INCOMPLETE";

        return "G01_COMPLETE";
    }
}
