namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave5 : ICommandPolicyCatalog
{
    public const int Wave5RecoveredMutationCommandCount = 3;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave5()
    {
        var historical = new RecoveredApiCommandCatalog();
        var entries = historical.All.Select(policy =>
            string.Equals(policy.CommandName, "ideas.submit-g04", StringComparison.OrdinalIgnoreCase)
                ? IdeaSubmitPolicy()
                : policy).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy IdeaSubmitPolicy() =>
        new(
            "ideas.submit-g04",
            new[] { "IDEA_OWNER" },
            new[] { "DRAFT", "RETURNED" },
            "P1-IDEA-SUBMIT-G04-REBASELINE-1.0",
            "IdeaSubmittedForEvaluation.v1",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: "IdeaSubmittedForEvaluation.v1"));
}
