namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave13 : ICommandPolicyCatalog
{
    public const int Wave13RecoveredMutationCommandCount = 32;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave13()
    {
        var historical = new RecoveredApiCommandCatalogWave11();
        var promoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "knowledge.create-draft",
            "knowledge.validate",
            "knowledge.publish"
        };

        var entries = historical.All.Where(x => !promoted.Contains(x.CommandName)).Concat(new[]
        {
            Static(
                "knowledge.create-draft",
                new[] { "NEED_OWNER", "IDEA_OWNER", "DOMAIN_EXPERT" },
                new[] { "REALIZED" },
                "KnowledgeDraftCreated.v1"),
            Outcome(
                "knowledge.validate",
                new[] { "KNOWLEDGE_STEWARD" },
                new[] { "DRAFT" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["APPROVE"] = "KnowledgeValidated.v1",
                    ["RETURN"] = "KnowledgeReturnedForCorrection.v1"
                }),
            Outcome(
                "knowledge.publish",
                new[] { "KNOWLEDGE_PUBLISHER" },
                new[] { "VALIDATED" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PUBLISH"] = "KnowledgePublished.v1",
                    ["RETURN"] = "KnowledgeReturnedForCorrection.v1"
                })
        }).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy Static(
        string command,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> states,
        string eventName) =>
        new(
            command,
            roles,
            states,
            "P1-KNOWLEDGE-ACR-P0-008-1.0",
            eventName,
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: eventName));

    private static CommandPolicy Outcome(
        string command,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> states,
        IReadOnlyDictionary<string, string> events) =>
        new(
            command,
            roles,
            states,
            "P1-KNOWLEDGE-ACR-P0-008-1.0",
            "OUTCOME_AWARE_EVENT_CONTRACT",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("OUTCOME", OutcomeEventNames: events));
}
