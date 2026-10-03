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
    IReadOnlyDictionary<string, string>? RuleFacts = null,
    string? WorkRoutingRole = null,
    long? StateMutationVersion = null);

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

public sealed record DecisionIntent(
    string DecisionType,
    string Outcome,
    string? Note = null,
    IReadOnlyDictionary<string, string>? Facts = null);

public sealed record EvaluationAssignmentIntent(
    string Role,
    string Scope,
    bool Required,
    string State = "PENDING");

public sealed record EvaluationPlanIntent(
    string State,
    int Version,
    IReadOnlyCollection<EvaluationAssignmentIntent> Assignments,
    string? DecisionRoute = null,
    string? DecisionRouteKind = null,
    string? DecisionMethod = null,
    string? GovernanceProfileId = null,
    string? GovernanceProfileVersion = null);

public sealed record DomainDecisionEnvelope(
    string DecisionId,
    string DecisionType,
    string Outcome,
    string AggregateId,
    long EntityVersion,
    string PersonId,
    string AssignmentId,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? Note = null,
    IReadOnlyDictionary<string, string>? Facts = null);

public sealed record G01RuleExecutionSnapshot(
    string RuleId,
    string Outcome);

public sealed record G01DecisionAuthorizationContext(
    string PrincipalId,
    string Role,
    string Capability,
    string Scope,
    string AssignmentId);

public sealed record G01DecisionSnapshotEnvelope(
    string SnapshotId,
    string DecisionId,
    string ObservationId,
    long ObservationVersion,
    string RuleSetId,
    string RuleSetVersion,
    string GateOutcome,
    IReadOnlyCollection<G01RuleExecutionSnapshot> RuleExecutions,
    G01DecisionAuthorizationContext AuthorizationContext,
    string DecisionOutcome,
    string? ReasonCode,
    string? Comment,
    DateTimeOffset CreatedAt,
    string SchemaVersion,
    string Fingerprint);

public sealed record EvaluationPlanEnvelope(
    string PlanId,
    string IdeaId,
    long IdeaVersion,
    int PlanVersion,
    string State,
    DateTimeOffset CreatedAt,
    string CorrelationId,
    DateTimeOffset? ReadyAt = null,
    string? DecisionRoute = null,
    string? DecisionRouteKind = null,
    string? DecisionMethod = null,
    string? GovernanceProfileId = null,
    string? GovernanceProfileVersion = null);

public sealed record EvaluationAssignmentEnvelope(
    string AssignmentId,
    string PlanId,
    string IdeaId,
    long IdeaVersion,
    string Role,
    string Scope,
    bool Required,
    string State,
    DateTimeOffset CreatedAt,
    string CorrelationId,
    int AssignmentVersion = 1,
    DateTimeOffset? CompletedAt = null,
    string? CompletedByPersonId = null,
    string? AuthorityAssignmentId = null,
    string? AssessmentSchemaId = null,
    string? AssessmentSchemaVersion = null,
    string? AssessmentOutcome = null);

public sealed record AssessmentSnapshotEnvelope(
    string SnapshotId,
    string EvaluationAssignmentId,
    string PlanId,
    string IdeaId,
    long IdeaVersion,
    string Role,
    string Scope,
    string SchemaId,
    string SchemaVersion,
    string Outcome,
    string NormalizedAssessmentJson,
    string ContentSha256,
    string PersonId,
    string AuthorityAssignmentId,
    DateTimeOffset CreatedAt,
    string CorrelationId);

public sealed record G04AssessmentEnvelope(
    string AssessmentId,
    string PlanId,
    string IdeaId,
    long IdeaVersion,
    int PlanVersion,
    string State,
    string RequiredAssignmentSnapshotSha256,
    DateTimeOffset CreatedAt,
    string CorrelationId,
    string? DecisionRoute = null,
    string? DecisionRouteKind = null,
    string? DecisionMethod = null,
    string? GovernanceProfileId = null,
    string? GovernanceProfileVersion = null,
    int DecisionVersion = 0);

public sealed record EvaluationCompletionCommand(
    string IdeaId,
    long ExpectedIdeaVersion,
    string PlanId,
    int ExpectedPlanVersion,
    string EvaluationAssignmentId,
    int ExpectedAssignmentVersion,
    string IdempotencyKey,
    string CorrelationId,
    string RawAssessmentJson,
    string? RequestedScope = null);

public sealed record AssessmentValidationResult(
    bool Passed,
    string Code,
    string SchemaId,
    string SchemaVersion,
    string? Outcome,
    string? NormalizedAssessmentJson,
    string? Detail = null)
{
    public static AssessmentValidationResult Fail(string code, string detail = "") =>
        new(false, code, string.Empty, string.Empty, null, null, detail);
}

public interface IEvaluatorAssessmentValidator
{
    AssessmentValidationResult Validate(string role, string rawAssessmentJson);
}

public sealed record EvaluationCompletionRequest(
    EvaluationCompletionCommand Command,
    AuthorityActor Actor,
    AggregateSnapshot Idea,
    EvaluationPlanEnvelope Plan,
    EvaluationAssignmentEnvelope Assignment,
    string IdempotencyFingerprint);

public sealed record EvaluationCompletionCommit(
    EvaluationAssignmentEnvelope AssignmentAfter,
    EvaluationPlanEnvelope PlanAfter,
    AssessmentSnapshotEnvelope AssessmentSnapshot,
    G04AssessmentEnvelope? G04Assessment,
    AuditEnvelope Audit,
    IReadOnlyCollection<OutboxEnvelope> OutboxEvents);

public sealed record MutationRequest(
    AuthorityCommand Command,
    AuthorityActor Actor,
    AggregateSnapshot Before,
    CommandPolicy Policy,
    string IdempotencyFingerprint);

public sealed record MutationCommit(
    AggregateSnapshot After,
    AuditEnvelope Audit,
    OutboxEnvelope Outbox,
    IReadOnlyCollection<DomainDecisionEnvelope>? Decisions = null,
    EvaluationPlanEnvelope? EvaluationPlan = null,
    IReadOnlyCollection<EvaluationAssignmentEnvelope>? EvaluationAssignments = null,
    G01DecisionSnapshotEnvelope? DecisionSnapshot = null);

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
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string>? Payload = null);

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

public interface IEvaluationWorkflowStore : IAuthorityStore
{
    ValueTask<EvaluationPlanEnvelope?> GetEvaluationPlanAsync(string planId, CancellationToken cancellationToken = default);
    ValueTask<EvaluationAssignmentEnvelope?> GetEvaluationAssignmentAsync(string evaluationAssignmentId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyCollection<EvaluationAssignmentEnvelope>> GetEvaluationAssignmentsForPlanAsync(string planId, CancellationToken cancellationToken = default);
    ValueTask<G04AssessmentEnvelope?> GetG04AssessmentForPlanAsync(string planId, CancellationToken cancellationToken = default);
    ValueTask<AuthorityResult> CommitEvaluationCompletionAsync(EvaluationCompletionRequest request, EvaluationCompletionCommit commit, CancellationToken cancellationToken = default);
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
    public ValueTask<RuleEvaluation> EvaluateAsync(
        AuthorityCommand command,
        AuthorityActor actor,
        AggregateSnapshot aggregate,
        CommandPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(command.CommandName, "g01.decide", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(RuleEvaluation.Pass());

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(command.RawBody);
            var root = document.RootElement;

            var outcome = root.TryGetProperty("outcome", out var outcomeProperty)
                ? outcomeProperty.GetString()
                : null;
            var reasonCode = root.TryGetProperty("reasonCode", out var reasonCodeProperty)
                ? reasonCodeProperty.GetString()
                : null;

            if ((string.Equals(outcome, "RETURN", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(outcome, "REJECT", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(reasonCode))
                return ValueTask.FromResult(RuleEvaluation.Fail(
                    "G01_REASON_CODE_REQUIRED",
                    $"ReasonCode is required for G01 outcome '{outcome}'."));

            if (root.TryGetProperty("ruleResults", out var ruleResults)
                && ruleResults.ValueKind == System.Text.Json.JsonValueKind.Object
                && ruleResults.TryGetProperty("R03", out var r03Result)
                && r03Result.ValueKind == System.Text.Json.JsonValueKind.Null
                && !root.TryGetProperty("r03Review", out _))
                return ValueTask.FromResult(RuleEvaluation.Fail(
                    "G01_R03_REVIEW_REQUIRED",
                    "R03 requires a completed human duplicate-history review."));

            if (root.TryGetProperty("r03Review", out var r03Review)
                && r03Review.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var reviewPerformed = r03Review.TryGetProperty("reviewPerformed", out var performedProperty)
                    && performedProperty.ValueKind == System.Text.Json.JsonValueKind.True
                    && performedProperty.GetBoolean();

                if (!reviewPerformed)
                    return ValueTask.FromResult(RuleEvaluation.Fail(
                        "G01_R03_REVIEW_REQUIRED",
                        "R03 requires a completed human duplicate-history review."));

                var reviewResult = r03Review.TryGetProperty("reviewResult", out var resultProperty)
                    ? resultProperty.GetString()
                    : null;

                if (string.Equals(reviewResult, "DIFFERENT", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(RuleEvaluation.Pass("G01_R03_PASS"));

                if (string.Equals(reviewResult, "DUPLICATE", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(RuleEvaluation.Fail(
                        "G01_R03_FAIL",
                        "R03 human review identified a duplicate."));

                if (string.Equals(reviewResult, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(RuleEvaluation.Pass("G01_R03_WARNING"));

                return ValueTask.FromResult(RuleEvaluation.Fail(
                    "G01_R03_REVIEW_RESULT_INVALID",
                    "R03 reviewResult must be DIFFERENT, DUPLICATE, or UNKNOWN."));
            }

            var hasOriginChannel = root.TryGetProperty("originChannel", out var originChannelProperty);
            var hasSourceType = root.TryGetProperty("sourceType", out var sourceTypeProperty);
            var hasUnit = root.TryGetProperty("unit", out var unitProperty);
            var hasTitle = root.TryGetProperty("title", out var titleProperty);
            var hasDescription = root.TryGetProperty("desc", out var descProperty);

            if (hasOriginChannel || (!hasSourceType && !hasUnit && !hasDescription && hasTitle))
            {
                var originChannel = hasOriginChannel ? originChannelProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(originChannel))
                    return ValueTask.FromResult(RuleEvaluation.Fail("G01_R02_FAIL"));

                var contextualOrigin = string.Equals(originChannel, "FIELD_VISIT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(originChannel, "CUSTOMER_VISIT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(originChannel, "AUDIT", StringComparison.OrdinalIgnoreCase);

                if (contextualOrigin
                    && (!root.TryGetProperty("originDetail", out var originDetailProperty)
                        || string.IsNullOrWhiteSpace(originDetailProperty.GetString())))
                    return ValueTask.FromResult(RuleEvaluation.Fail("G01_R02_FAIL"));

                return ValueTask.FromResult(RuleEvaluation.Pass("G01_R02_PASS"));
            }

            if (hasSourceType || hasUnit)
            {
                var sourceType = hasSourceType ? sourceTypeProperty.GetString() : null;
                var unit = hasUnit ? unitProperty.GetString() : null;

                if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(unit))
                    return ValueTask.FromResult(RuleEvaluation.Fail("G01_R04_FAIL"));

                return ValueTask.FromResult(RuleEvaluation.Pass("G01_R04_PASS"));
            }

            if (hasTitle || hasDescription)
            {
                var title = hasTitle ? titleProperty.GetString() : null;
                var description = hasDescription ? descProperty.GetString() : null;

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description) || description.Length < 15)
                    return ValueTask.FromResult(RuleEvaluation.Fail("G01_R05_FAIL"));

                return ValueTask.FromResult(RuleEvaluation.Pass("G01_R05_PASS"));
            }

            return ValueTask.FromResult(RuleEvaluation.Pass("G01_REASON_CODE_VALID"));
        }
        catch (System.Text.Json.JsonException)
        {
            return ValueTask.FromResult(RuleEvaluation.Fail(
                "G01_DECISION_PAYLOAD_INVALID",
                "G01 decision payload must be valid JSON."));
        }
    }
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
