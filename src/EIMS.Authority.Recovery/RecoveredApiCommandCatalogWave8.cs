namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave8 : ICommandPolicyCatalog
{
    public const int Wave8RecoveredMutationCommandCount = 6;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave8()
    {
        var historical = new RecoveredApiCommandCatalogWave7();
        var entries = historical.All.Select(policy =>
            string.Equals(policy.CommandName, "g04.final-decision", StringComparison.OrdinalIgnoreCase)
                ? G04FinalDecisionPolicy()
                : policy).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy G04FinalDecisionPolicy() =>
        new(
            "g04.final-decision",
            new[] { "IDEA_DECISION" },
            new[] { "UNDER_REVIEW" },
            "P1-G04-FINAL-ACR-P0-007-1.0",
            "OUTCOME_AWARE_EVENT_CONTRACT",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding(
                "OUTCOME",
                OutcomeEventNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["APPROVE"] = "IdeaApprovedForPortfolio.v1",
                    ["RETURN"] = "IdeaReturnedFromG04.v1",
                    ["HOLD"] = "IdeaHeldAtG04.v1",
                    ["REJECT"] = "IdeaRejectedAtG04.v1"
                }));
}
