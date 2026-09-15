namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave6 : ICommandPolicyCatalog
{
    public const int Wave6RecoveredMutationCommandCount = 4;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave6()
    {
        var wave5 = new RecoveredApiCommandCatalogWave5();
        var entries = wave5.All.Select(policy =>
            string.Equals(policy.CommandName, "evaluation-assignments.complete", StringComparison.OrdinalIgnoreCase)
                ? EvaluationCompletionPolicy()
                : policy).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy EvaluationCompletionPolicy() =>
        new(
            "evaluation-assignments.complete",
            new[] { "MATCH_ASSIGNMENT_ROLE" },
            new[] { "PENDING" },
            "P1-EVALUATION-COMPLETE-REBASELINE-1.0",
            "EvaluationAssignmentCompleted.v1",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: "EvaluationAssignmentCompleted.v1"));
}
