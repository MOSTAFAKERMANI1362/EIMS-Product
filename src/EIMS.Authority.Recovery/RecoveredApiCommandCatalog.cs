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
            P("g01.decide", "INTAKE_STEWARD", "G01-RS-1.0", "G01DecisionRecorded.v1"),
            P("g02.decide", "CASE_REVIEWER", "G02-RS-1.0", "G02DecisionRecorded.v1"),
            P("needs.submit-g03", "NEED_OWNER", "G03-RS-1.0", "NeedSubmittedForG03.v1"),
            P("needs.g03-decision", "NEED_REVIEWER", "G03-RS-1.0", "G03DecisionRecorded.v1"),
            P("ideas.submit-g04", "IDEA_OWNER", "G04-RS-1.0", "IdeaSubmittedForG04.v1"),
            P("evaluation-assignments.complete", "MATCH_ASSIGNMENT_ROLE", "G04-DYNAMIC-EVAL-1.0", "EvaluationAssignmentCompleted.v1"),
            P("g04.vote", "G04_COMMITTEE_MEMBER", "G04-RS-1.0", "G04VoteRecorded.v1"),
            P("g04.final-decision", "IDEA_DECISION", "G04-RS-1.0", "G04FinalDecisionRecorded.v1"),
            P("portfolio.assign-accept", "PORTFOLIO_MANAGER", "EP06-PORTFOLIO-1.0", "PortfolioMembershipAccepted.v1"),
            P("executions.prepare", "EXECUTION_OWNER", "EP07-EXECUTION-1.0", "ExecutionPrepared.v1"),
            P("executions.progress", "EXECUTION_OWNER", "EP07-EXECUTION-1.0", "ExecutionProgressRecorded.v1"),
            P("executions.submit-completion", "EXECUTION_OWNER", "EP07-EXECUTION-1.0", "ExecutionCompletionSubmitted.v1"),
            P("executions.completion-review", "EXECUTION_COMPLETION_REVIEWER", "EP07-EXECUTION-1.0", "ExecutionCompletionReviewed.v1"),
            P("benefits.accept", "BENEFIT_OWNER", "EP08-BENEFIT-1.0", "BenefitObligationAccepted.v1"),
            P("benefits.measure", "BENEFIT_OWNER_OR_AUTHORIZED_DATA_PROVIDER", "EP08-BENEFIT-1.0", "BenefitMeasurementRecorded.v1"),
            P("benefits.verify", "BENEFIT_VERIFIER", "EP08-BENEFIT-1.0", "BenefitVerified.v1"),
            P("benefits.attribution", "BENEFIT_VERIFIER", "EP08-BENEFIT-1.0", "BenefitAttributionValidated.v1"),
            P("benefits.realize", "BENEFIT_OWNER", "EP08-BENEFIT-1.0", "BenefitRealizationRecorded.v1"),
            P("knowledge.validate", "KNOWLEDGE_STEWARD", "EP09-KNOWLEDGE-1.0", "KnowledgeValidated.v1"),
            P("knowledge.publish", "KNOWLEDGE_PUBLISHER", "EP09-KNOWLEDGE-1.0", "KnowledgePublished.v1"),
            P("rewards.decide", "REWARD_COMMITTEE", "REWARD-1.0", "RewardDecisionRecorded.v1")
        };
        _policies = entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(entries);
    }

    public IReadOnlyCollection<CommandPolicy> All { get; }

    public bool TryGet(string commandName, out CommandPolicy policy) =>
        _policies.TryGetValue(commandName, out policy!);

    public bool IsCatalogComplete => All.Count == CompletionReviewDeclaredCommandCount;

    private static CommandPolicy P(string name, string role, string ruleSet, string eventName) =>
        new(name,
            new[] { role },
            Array.Empty<string>(),
            ruleSet,
            eventName,
            StateContractRecovered: false,
            RuleContractRecovered: true);
}
