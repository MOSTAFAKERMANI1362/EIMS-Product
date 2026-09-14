namespace EIMS.Authority.Recovery;

public sealed record AuthorityActor(
    string PersonId,
    string NetworkIdentity,
    string IdentitySource,
    string AssignmentId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes);

public sealed record AuthorityCommand(
    string CommandName,
    string AggregateId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string RawBody,
    string? RequestedScope = null);

public sealed record AggregateSnapshot(
    string AggregateId,
    string AggregateType,
    string State,
    long Version,
    string? OwnerPersonId = null,
    string? OwnerRole = null,
    string? Scope = null,
    IReadOnlyDictionary<string, string>? RuleFacts = null);

public sealed record CommandEventBinding(
    string Kind,
    string? StaticEventName = null,
    IReadOnlyDictionary<string, string>? OutcomeEventNames = null)
{
    public string? Resolve(string? outcome = null)
    {
        if (string.Equals(Kind, "STATIC", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(StaticEventName) ? null : StaticEventName;

        if (!string.Equals(Kind, "OUTCOME", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(outcome)
            || OutcomeEventNames is null)
            return null;

        foreach (var pair in OutcomeEventNames)
        {
            if (string.Equals(pair.Key, outcome, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value;
        }

        return null;
    }

    public bool IsValid
    {
        get
        {
            if (string.Equals(Kind, "STATIC", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(StaticEventName)
                    && (OutcomeEventNames is null || OutcomeEventNames.Count == 0);

            if (!string.Equals(Kind, "OUTCOME", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(StaticEventName)
                || OutcomeEventNames is null
                || OutcomeEventNames.Count == 0)
                return false;

            return OutcomeEventNames.Keys.All(x => !string.IsNullOrWhiteSpace(x))
                && OutcomeEventNames.Values.All(x => !string.IsNullOrWhiteSpace(x))
                && OutcomeEventNames.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == OutcomeEventNames.Count;
        }
    }
}

public sealed record CommandPolicy(
    string CommandName,
    IReadOnlyCollection<string> RequiredRoles,
    IReadOnlyCollection<string> AllowedStates,
    string RuleSet,
    string EventName,
    bool StateContractRecovered = true,
    bool RuleContractRecovered = true,
    bool EventContractRecovered = true,
    bool MutationContractRecovered = true,
    CommandEventBinding? EventBinding = null)
{
    public string? ResolveEventName(string? outcome = null)
    {
        if (EventBinding is not null)
            return EventBinding.Resolve(outcome);

        if (string.IsNullOrWhiteSpace(EventName)
            || string.Equals(EventName, "UNRECOVERED_EVENT_IDENTITY", StringComparison.Ordinal)
            || string.Equals(EventName, "OUTCOME_AWARE_EVENT_CONTRACT", StringComparison.Ordinal))
            return null;

        return EventName;
    }
}

public sealed record RuleEvaluation(bool Passed, string Code, string? Detail = null)
{
    public static RuleEvaluation Pass(string code = "RULES_PASS") => new(true, code);
    public static RuleEvaluation Fail(string code, string? detail = null) => new(false, code, detail);
}

public sealed record SodEvaluation(bool Passed, string Code, string? Detail = null)
{
    public static SodEvaluation Pass() => new(true, "SOD_PASS");
    public static SodEvaluation Fail(string code, string? detail = null) => new(false, code, detail);
}

public sealed record AuthorityResult(
    int HttpStatus,
    string Code,
    bool Allowed,
    bool StateMutated,
    bool IdempotentReplay,
    long? NewVersion,
    string CorrelationId,
    IReadOnlyCollection<string> EmittedEvents,
    string? Detail = null)
{
    public static AuthorityResult Deny(int status, string code, string correlationId, string? detail = null) =>
        new(status, code, false, false, false, null, correlationId, Array.Empty<string>(), detail);
}

public sealed record MutationRequest(
    AuthorityCommand Command,
    AuthorityActor Actor,
    AggregateSnapshot Before,
    CommandPolicy Policy,
    string IdempotencyFingerprint);

public sealed record MutationCommit(
    AggregateSnapshot After,
    AuditEnvelope Audit,
    OutboxEnvelope Outbox);

public sealed record AuditEnvelope(
    string AuditId,
    string PersonId,
    string NetworkIdentity,
    string IdentitySource,
    IReadOnlyCollection<string> Roles,
    string Assignment,
    string AggregateId,
    long EntityVersion,
    string RuleSet,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string CommandName);

public sealed record OutboxEnvelope(
    string MessageId,
    string EventName,
    string AggregateId,
    long AggregateVersion,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record IdempotencyRecord(
    string CommandName,
    string AggregateId,
    string IdempotencyKey,
    string Fingerprint,
    AuthorityResult Result);

public interface ICommandPolicyCatalog
{
    bool TryGet(string commandName, out CommandPolicy policy);
    IReadOnlyCollection<CommandPolicy> All { get; }
}

public interface IAuthorityStore
{
    ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default);
    ValueTask<IdempotencyRecord?> GetIdempotencyAsync(string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitAsync(MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default);
}

public interface IRuleEvaluator
{
    ValueTask<RuleEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default);
}

public interface ISodEvaluator
{
    ValueTask<SodEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default);
}

public sealed class PassRuleEvaluator : IRuleEvaluator
{
    public ValueTask<RuleEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RuleEvaluation.Pass());
}

public sealed class BaselineSodEvaluator : ISodEvaluator
{
    public ValueTask<SodEvaluation> EvaluateAsync(AuthorityCommand command, AuthorityActor actor, AggregateSnapshot aggregate, CommandPolicy policy, CancellationToken cancellationToken = default)
    {
        var roles = new HashSet<string>(actor.Roles, StringComparer.OrdinalIgnoreCase);

        if (command.CommandName.Contains("completion-review", StringComparison.OrdinalIgnoreCase)
            && roles.Contains("EXECUTION_OWNER")
            && string.Equals(aggregate.OwnerPersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(SodEvaluation.Fail("SOD_EXECUTION_SELF_COMPLETION", "Execution Owner cannot independently approve own completion."));

        if (command.CommandName.Contains("g04.final-decision", StringComparison.OrdinalIgnoreCase)
            && roles.Contains("G04_COMMITTEE_MEMBER"))
            return ValueTask.FromResult(SodEvaluation.Fail("SOD_G04_MEMBER_NOT_FINAL_AUTHORITY", "A G04 committee member cannot act as final G04 decision authority for the same decision context."));

        if (command.CommandName.Contains("knowledge.publish", StringComparison.OrdinalIgnoreCase)
            && roles.Contains("KNOWLEDGE_STEWARD"))
            return ValueTask.FromResult(SodEvaluation.Fail("SOD_KNOWLEDGE_STEWARD_NOT_PUBLISHER"));

        if (command.CommandName.Contains("rewards.decide", StringComparison.OrdinalIgnoreCase)
            && roles.Contains("BENEFIT_OWNER"))
            return ValueTask.FromResult(SodEvaluation.Fail("SOD_BENEFIT_OWNER_NOT_REWARD_COMMITTEE"));

        if (command.CommandName.Contains("needs.g03-decision", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(aggregate.OwnerPersonId)
            && string.Equals(aggregate.OwnerPersonId, actor.PersonId, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(SodEvaluation.Fail("SOD_G03_NEED_OWNER_SELF_REVIEW", "Need Owner cannot independently approve or return the same Need at G03."));

        return ValueTask.FromResult(SodEvaluation.Pass());
    }
}
