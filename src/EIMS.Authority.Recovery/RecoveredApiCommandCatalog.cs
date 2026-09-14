namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalog : ICommandPolicyCatalog
{
    public const int CompletionReviewDeclaredCommandCount = 28;
    public const int RecoveredApiCommandCount = 21;

    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalog()
    {
        var entries = new[]
        {
            P("g01.decide", "INTAKE_STEWARD"),
            P("g02.decide", "CASE_REVIEWER"),
            P("needs.submit-g03", "NEED_OWNER"),
            P("needs.g03-decision", "NEED_REVIEWER"),
            P("ideas.submit-g04", "IDEA_OWNER"),
            P("evaluation-assignments.complete", "MATCH_ASSIGNMENT_ROLE"),
            P("g04.vote", "G04_COMMITTEE_MEMBER"),
            P("g04.final-decision", "IDEA_DECISION"),
            P("portfolio.assign-accept", "PORTFOLIO_MANAGER"),
            P("executions.prepare", "EXECUTION_OWNER"),
            P("executions.progress", "EXECUTION_OWNER"),
            P("executions.submit-completion", "EXECUTION_OWNER"),
            P("executions.completion-review", "EXECUTION_COMPLETION_REVIEWER"),
            P("benefits.accept", "BENEFIT_OWNER"),
            P("benefits.measure", "BENEFIT_OWNER_OR_AUTHORIZED_DATA_PROVIDER"),
            P("benefits.verify", "BENEFIT_VERIFIER"),
            P("benefits.attribution", "BENEFIT_VERIFIER"),
            P("benefits.realize", "BENEFIT_OWNER"),
            P("knowledge.validate", "KNOWLEDGE_STEWARD"),
            P("knowledge.publish", "KNOWLEDGE_PUBLISHER"),
            P("rewards.decide", "REWARD_COMMITTEE")
        };
        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    public bool IsCatalogComplete => All.Count == CompletionReviewDeclaredCommandCount;

    private static CommandPolicy P(string name, string role) =>
        new(name,
            new[] { role },
            Array.Empty<string>(),
            "UNRECOVERED_RULESET",
            "UNRECOVERED_EVENT_IDENTITY",
            StateContractRecovered: false,
            RuleContractRecovered: false);
}
