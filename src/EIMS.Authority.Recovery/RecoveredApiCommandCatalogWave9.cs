namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave9 : ICommandPolicyCatalog
{
    public const int Wave9RecoveredMutationCommandCount = 12;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave9()
    {
        var historical = new RecoveredApiCommandCatalogWave8();
        var entries = historical.All.Concat(new[]
        {
            Static("portfolio.assign-candidate", "PortfolioCandidateAssigned.v1", "UNASSIGNED_CANDIDATE"),
            Outcome("portfolio.membership-decision", new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ACCEPTED"] = "PortfolioMembershipAccepted.v1",
                ["REJECTED"] = "PortfolioMembershipRejected.v1",
                ["DEFERRED"] = "PortfolioMembershipDeferred.v1"
            }, "PENDING_ASSIGNMENT"),
            Static("portfolio.generate-execution-recommendation", "ExecutionRecommendationGenerated.v1", "ACCEPTED"),
            Static("portfolio.approve-execution-recommendation", "ExecutionRecommendationApproved.v1", "ACCEPTED"),
            Static("portfolio.bind-approved-baseline", "PortfolioBaselineBoundToRecommendation.v1", "ACCEPTED"),
            Static("portfolio.request-execution-handoff", "ExecutionHandoffRequested.v1", "ACCEPTED")
        }).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy Static(string command, string eventName, params string[] states) =>
        new(
            command,
            new[] { "PORTFOLIO_MANAGER" },
            Array.AsReadOnly(states),
            "P1-PORTFOLIO-ACR-P0-008-1.0",
            eventName,
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: eventName));

    private static CommandPolicy Outcome(
        string command,
        IReadOnlyDictionary<string,string> events,
        params string[] states) =>
        new(
            command,
            new[] { "PORTFOLIO_MANAGER" },
            Array.AsReadOnly(states),
            "P1-PORTFOLIO-ACR-P0-008-1.0",
            "OUTCOME_AWARE_EVENT_CONTRACT",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("OUTCOME", OutcomeEventNames: events));
}
