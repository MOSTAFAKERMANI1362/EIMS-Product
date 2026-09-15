namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalogWave7 : ICommandPolicyCatalog
{
    public const int Wave7RecoveredMutationCommandCount = 5;
    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalogWave7()
    {
        var historical = new RecoveredApiCommandCatalogWave6();
        var entries = historical.All.Select(policy =>
            string.Equals(policy.CommandName, "g04.vote", StringComparison.OrdinalIgnoreCase)
                ? G04VotePolicy()
                : policy).ToArray();

        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    private static CommandPolicy G04VotePolicy() =>
        new(
            "g04.vote",
            new[] { "G04_COMMITTEE_MEMBER" },
            new[] { "PENDING" },
            "P1-G04-VOTE-ACR-P0-006-1.0",
            "G04CommitteeVoteRecorded.v1",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: true,
            MutationContractRecovered: true,
            EventBinding: new CommandEventBinding("STATIC", StaticEventName: "G04CommitteeVoteRecorded.v1"));
}
