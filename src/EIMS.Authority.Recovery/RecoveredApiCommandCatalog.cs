namespace EIMS.Authority.Recovery;

public sealed class RecoveredApiCommandCatalog : ICommandPolicyCatalog
{
    public const int CompletionReviewDeclaredCommandCount = 28;
    public const int RecoveredApiCommandCount = 21;
    public const int Wave1StateBoundCommandCount = 6;
    public const int Wave2RuleBoundCommandCount = 2;

    private readonly IReadOnlyDictionary<string, CommandPolicy> _policies;

    public RecoveredApiCommandCatalog()
    {
        var entries = new[]
        {
            PState("g01.decide", "INTAKE_STEWARD", "SUBMITTED", "SUBMITTED_FOR_G01"),
            PState("g02.decide", "CASE_REVIEWER", "UNDER_REVIEW"),
            PG03Rule("needs.submit-g03", "NEED_OWNER", "P1-G03-SUBMIT-REBASELINE-1.0", "DRAFT"),
            PG03Rule("needs.g03-decision", "NEED_REVIEWER", "P1-G03-DECISION-REBASELINE-1.0", "PENDING_G03_REVIEW"),
            PState("ideas.submit-g04", "IDEA_OWNER", "DRAFT", "RETURNED"),
            P("evaluation-assignments.complete", "MATCH_ASSIGNMENT_ROLE"),
            P("g04.vote", "G04_COMMITTEE_MEMBER"),
            PState("g04.final-decision", "IDEA_DECISION", "UNDER_REVIEW"),
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
            RuleContractRecovered: false,
            EventContractRecovered: false,
            MutationContractRecovered: false);

    private static CommandPolicy PState(string name, string role, params string[] allowedStates) =>
        new(name,
            new[] { role },
            Array.AsReadOnly(allowedStates),
            "UNRECOVERED_RULESET",
            "UNRECOVERED_EVENT_IDENTITY",
            StateContractRecovered: true,
            RuleContractRecovered: false,
            EventContractRecovered: false,
            MutationContractRecovered: false);

    private static CommandPolicy PG03Rule(string name, string role, string ruleSet, params string[] allowedStates) =>
        new(name,
            new[] { role },
            Array.AsReadOnly(allowedStates),
            ruleSet,
            "UNRECOVERED_EVENT_IDENTITY",
            StateContractRecovered: true,
            RuleContractRecovered: true,
            EventContractRecovered: false,
            MutationContractRecovered: false);
}
