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

public sealed record CommandPolicy(
    string CommandName,
    IReadOnlyCollection<string> RequiredRoles,
    IReadOnlyCollection<string> AllowedStates,
    string RuleSet,
    string EventName,
    bool StateContractRecovered = true,
    bool RuleContractRecovered = true,
    bool EventContractRecovered = true,
    bool MutationContractRecovered = true);

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
