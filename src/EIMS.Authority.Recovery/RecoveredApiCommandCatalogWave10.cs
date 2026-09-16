namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave10 : ICommandPolicyCatalog
{
    public const int Wave10RecoveredMutationCommandCount = 21;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave10()
    {
        var historical = new RecoveredApiCommandCatalogWave9();
        var promoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "executions.progress",
            "executions.submit-completion",
            "executions.completion-review"
        };

        var entries = historical.All.Where(x => !promoted.Contains(x.CommandName)).Concat(new[]
        {
            StaticOwner("executions.approve-charter", "ExecutionCharterApproved.v1", "PLANNING"),
            StaticOwner("executions.approve-plan-baseline", "ExecutionPlanBaselineApproved.v1", "PLANNING"),
            StaticOwner("executions.start", "ExecutionStarted.v1", "PLANNING"),
            StaticOwner("executions.progress", "ExecutionProgressRecorded.v1", "ACTIVE"),
            StaticOwner("executions.submit-completion", "ExecutionCompletionSubmitted.v1", "ACTIVE"),
            OutcomeReviewer("executions.completion-review", new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["APPROVE"] = "ExecutionCompletionApproved.v1",
                ["RETURN"] = "ExecutionCompletionReturned.v1"
            }, "COMPLETION_REVIEW"),
            StaticOwner("executions.request-benefit-handoff", "BenefitHandoffRequested.v1", "COMPLETED"),
            StaticOwner("executions.begin-closure", "ExecutionClosureStarted.v1", "COMPLETED"),
            StaticOwner("executions.close", "ExecutionClosed.v1", "CLOSURE_IN_PROGRESS")
        }).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy StaticOwner(string command, string eventName, params string[] states) =>
        new(
            command,
            new[] { "EXECUTION_OWNER" },
            Array.AsReadOnly(states),
            "P1-EXECUTION-ACR-P0-008-1.0",
            eventName,
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: eventName));

    private static CommandPolicy OutcomeReviewer(
        string command,
        IReadOnlyDictionary<string,string> events,
        params string[] states) =>
        new(
            command,
            new[] { "EXECUTION_COMPLETION_REVIEWER" },
            Array.AsReadOnly(states),
            "P1-EXECUTION-ACR-P0-008-1.0",
            "OUTCOME_AWARE_EVENT_CONTRACT",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("OUTCOME", OutcomeEventNames: events));
}
