using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

/// <summary>
/// Wave 10 server-authoritative Execution runtime derived from ACR-P0-008.
/// Execution ownership is resolved from server-side evidence; client role/scope/ownership claims are never authority.
/// </summary>
public sealed class ExecutionServiceWave10(
    IExecutionWave10Store store,
    IExecutionOwnershipEvidenceProviderWave10 ownershipProvider,
    IBenefitAcceptanceEvidenceProviderWave10 benefitAcceptanceProvider,
    ICommandPolicyCatalog? catalog = null,
    TimeProvider? clock = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave10();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<AuthorityResult> ExecuteAsync(
        ExecutionCommandWave10 command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(command);
        if (string.IsNullOrWhiteSpace(normalized.CommandName)
            || string.IsNullOrWhiteSpace(normalized.ExecutionId)
            || string.IsNullOrWhiteSpace(normalized.IdempotencyKey)
            || string.IsNullOrWhiteSpace(normalized.CorrelationId)
            || normalized.ExpectedVersion <= 0)
            return AuthorityResult.Deny(400, "P1_EXECUTION_COMMAND_INVALID", normalized.CorrelationId ?? string.Empty);

        if (!_catalog.TryGet(normalized.CommandName, out var policy)
            || !policy.MutationContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered)
            return AuthorityResult.Deny(503, "P1_EXECUTION_COMMAND_NOT_BOUND", normalized.CorrelationId);

        if (!ValidateActorIdentityAndRole(actor, normalized, policy, out var actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, normalized.CorrelationId);
        var resolvedActor = actor!;

        var ownership = await ownershipProvider.ResolveAsync(normalized.ExecutionId, cancellationToken);
        if (ownership is null
            || !string.Equals(ownership.ExecutionId, normalized.ExecutionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ownership.OwnerPersonId)
            || string.IsNullOrWhiteSpace(ownership.OwnerAssignmentId)
            || string.IsNullOrWhiteSpace(ownership.Scope)
            || string.IsNullOrWhiteSpace(ownership.EvidenceRef)
            || string.IsNullOrWhiteSpace(ownership.EvidenceVersion))
            return AuthorityResult.Deny(409, "P1_EXECUTION_OWNERSHIP_EVIDENCE_REQUIRED", normalized.CorrelationId);

        if (!ValidateAuthorityContext(resolvedActor, normalized, policy, ownership, out actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, normalized.CorrelationId);

        var fingerprint = Fingerprint(normalized, resolvedActor);
        var prior = await store.GetExecutionIdempotencyAsync(
            normalized.CommandName,
            normalized.ExecutionId,
            normalized.IdempotencyKey,
            cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", normalized.CorrelationId);
            return prior.Result with
            {
                IdempotentReplay = true,
                StateMutated = false,
                CorrelationId = normalized.CorrelationId
            };
        }

        var before = await store.GetExecutionAsync(normalized.ExecutionId, cancellationToken);
        if (before is null)
            return AuthorityResult.Deny(404, "P1_EXECUTION_NOT_FOUND", normalized.CorrelationId);
        if (before.Execution.Version != normalized.ExpectedVersion)
            return AuthorityResult.Deny(409, "P1_EXECUTION_VERSION_CONFLICT", normalized.CorrelationId);

        if (string.Equals(normalized.CommandName, "executions.completion-review", StringComparison.Ordinal)
            && (string.Equals(before.Execution.CompletionSubmittedByPersonId, resolvedActor.PersonId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ownership.OwnerPersonId, resolvedActor.PersonId, StringComparison.OrdinalIgnoreCase)))
            return AuthorityResult.Deny(403, "SOD_EXECUTION_SELF_COMPLETION", normalized.CorrelationId,
                "Execution Owner/completion submitter cannot review the same completion.");

        BenefitAcceptanceEvidenceWave10? benefitAcceptance = null;
        if (string.Equals(normalized.CommandName, "executions.begin-closure", StringComparison.Ordinal))
        {
            if (before.BenefitObligation is null)
                return AuthorityResult.Deny(409, "P1_EXECUTION_BENEFIT_OBLIGATION_REQUIRED", normalized.CorrelationId);
            benefitAcceptance = await benefitAcceptanceProvider.ResolveAsync(before.BenefitObligation.BenefitId, cancellationToken);
            if (benefitAcceptance is null
                || !string.Equals(benefitAcceptance.BenefitId, before.BenefitObligation.BenefitId, StringComparison.Ordinal)
                || !benefitAcceptance.AcceptedByBenefitOwner
                || string.IsNullOrWhiteSpace(benefitAcceptance.EvidenceRef)
                || string.IsNullOrWhiteSpace(benefitAcceptance.EvidenceVersion))
                return AuthorityResult.Deny(409, "P1_EXECUTION_BENEFIT_ACCEPTANCE_REQUIRED", normalized.CorrelationId);
        }

        var plan = Plan(normalized, resolvedActor, ownership, benefitAcceptance, before, policy);
        if (!plan.Allowed || plan.Commit is null)
            return AuthorityResult.Deny(plan.Status, plan.Code, normalized.CorrelationId, plan.Detail);

        return await store.CommitExecutionCommandAsync(
            new ExecutionCommandRequestWave10(normalized, resolvedActor, before, policy, ownership, fingerprint),
            plan.Commit,
            cancellationToken);
    }

    private Planned Plan(
        ExecutionCommandWave10 command,
        AuthorityActor actor,
        ExecutionOwnershipEvidenceWave10 ownership,
        BenefitAcceptanceEvidenceWave10? benefitAcceptance,
        ExecutionThreadEnvelopeWave10 before,
        CommandPolicy policy)
    {
        var now = _clock.GetUtcNow();
        var execution = before.Execution;
        var nextVersion = execution.Version + 1;
        ExecutionEnvelopeWave10 afterExecution;
        BenefitObligationEnvelopeWave10? afterBenefit = before.BenefitObligation;
        IReadOnlyCollection<OutboxEnvelope> events;

        switch (command.CommandName)
        {
            case "executions.approve-charter":
                if (!State(execution, "PLANNING") || execution.CharterApproved)
                    return Planned.Deny(409, "P1_EXECUTION_CHARTER_STATE_INVALID");
                afterExecution = execution with
                {
                    CharterApproved = true,
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionCharterApproved.v1", afterExecution, command, now,
                    new Dictionary<string,string> { ["charterApproved"] = "true" });
                break;

            case "executions.approve-plan-baseline":
                if (!State(execution, "PLANNING") || execution.PlanApproved)
                    return Planned.Deny(409, "P1_EXECUTION_PLAN_STATE_INVALID");
                afterExecution = execution with
                {
                    PlanApproved = true,
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionPlanBaselineApproved.v1", afterExecution, command, now,
                    new Dictionary<string,string> { ["planApproved"] = "true" });
                break;

            case "executions.start":
                if (!State(execution, "PLANNING"))
                    return Planned.Deny(409, "P1_EXECUTION_START_STATE_INVALID");
                if (!execution.CharterApproved || !execution.PlanApproved)
                    return Planned.Deny(409, "P1_EXECUTION_START_APPROVALS_REQUIRED");
                afterExecution = execution with
                {
                    State = "ACTIVE",
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionStarted.v1", afterExecution, command, now,
                    new Dictionary<string,string> { ["state"] = "ACTIVE" });
                break;

            case "executions.progress":
                if (!State(execution, "ACTIVE"))
                    return Planned.Deny(409, "P1_EXECUTION_PROGRESS_STATE_INVALID");
                if (!command.ProgressPercent.HasValue || command.ProgressPercent is < 0 or > 100)
                    return Planned.Deny(400, "P1_EXECUTION_PROGRESS_INVALID");
                afterExecution = execution with
                {
                    ProgressPercent = command.ProgressPercent.Value,
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionProgressRecorded.v1", afterExecution, command, now,
                    new Dictionary<string,string>
                    {
                        ["progressPercent"] = afterExecution.ProgressPercent.ToString(),
                        ["state"] = "ACTIVE"
                    });
                break;

            case "executions.submit-completion":
                if (!State(execution, "ACTIVE"))
                    return Planned.Deny(409, "P1_EXECUTION_COMPLETION_SUBMIT_STATE_INVALID");
                if (execution.ProgressPercent != 100)
                    return Planned.Deny(409, "P1_EXECUTION_PROGRESS_100_REQUIRED");
                if (string.IsNullOrWhiteSpace(command.CompletionDossierRef))
                    return Planned.Deny(400, "P1_EXECUTION_COMPLETION_DOSSIER_REQUIRED");
                afterExecution = execution with
                {
                    State = "COMPLETION_REVIEW",
                    Version = nextVersion,
                    CompletionDossierRef = command.CompletionDossierRef,
                    CompletionApproved = false,
                    CompletionSubmittedByPersonId = actor.PersonId,
                    CompletionSubmittedByAssignmentId = actor.AssignmentId,
                    CompletionReviewedByPersonId = null,
                    CompletionReviewDecision = null,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionCompletionSubmitted.v1", afterExecution, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = "COMPLETION_REVIEW",
                        ["completionDossierRef"] = afterExecution.CompletionDossierRef!
                    });
                break;

            case "executions.completion-review":
                if (!State(execution, "COMPLETION_REVIEW")
                    || string.IsNullOrWhiteSpace(execution.CompletionDossierRef)
                    || string.IsNullOrWhiteSpace(execution.CompletionSubmittedByPersonId))
                    return Planned.Deny(409, "P1_EXECUTION_COMPLETION_REVIEW_STATE_INVALID");
                if (command.CompletionDecision is not ("APPROVE" or "RETURN"))
                    return Planned.Deny(400, "P1_EXECUTION_COMPLETION_DECISION_INVALID");

                var approved = command.CompletionDecision == "APPROVE";
                afterExecution = execution with
                {
                    State = approved ? "COMPLETED" : "ACTIVE",
                    Version = nextVersion,
                    CompletionApproved = approved,
                    CompletionReviewedByPersonId = actor.PersonId,
                    CompletionReviewDecision = command.CompletionDecision,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                var reviewEvent = approved ? "ExecutionCompletionApproved.v1" : "ExecutionCompletionReturned.v1";
                events = One(reviewEvent, afterExecution, command, now,
                    new Dictionary<string,string>
                    {
                        ["decision"] = command.CompletionDecision,
                        ["state"] = afterExecution.State
                    });
                break;

            case "executions.request-benefit-handoff":
                if (!State(execution, "COMPLETED") || !execution.CompletionApproved)
                    return Planned.Deny(409, "P1_EXECUTION_COMPLETION_APPROVAL_REQUIRED");
                if (before.BenefitObligation is not null || !string.IsNullOrWhiteSpace(execution.BenefitId))
                    return Planned.Deny(409, "P1_EXECUTION_BENEFIT_ALREADY_CREATED");

                var benefit = new BenefitObligationEnvelopeWave10(
                    "BEN-" + Guid.NewGuid().ToString("N"),
                    execution.ExecutionId,
                    execution.IdeaId,
                    execution.ApprovedIdeaVersion,
                    "OBLIGATION_PENDING_ACCEPTANCE",
                    1,
                    now,
                    command.CorrelationId);
                afterBenefit = benefit;
                afterExecution = execution with
                {
                    BenefitId = benefit.BenefitId,
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = new[]
                {
                    Event("BenefitHandoffRequested.v1", execution.ExecutionId, nextVersion, command.CorrelationId, now,
                        new Dictionary<string,string>
                        {
                            ["executionId"] = execution.ExecutionId,
                            ["benefitId"] = benefit.BenefitId,
                            ["completionDossierRef"] = execution.CompletionDossierRef!
                        }),
                    Event("BenefitObligationCreated.v1", benefit.BenefitId, 1, command.CorrelationId, now,
                        new Dictionary<string,string>
                        {
                            ["benefitId"] = benefit.BenefitId,
                            ["executionId"] = execution.ExecutionId,
                            ["ideaId"] = execution.IdeaId,
                            ["approvedIdeaVersion"] = execution.ApprovedIdeaVersion.ToString(),
                            ["state"] = benefit.State
                        })
                };
                break;

            case "executions.begin-closure":
                if (!State(execution, "COMPLETED") || before.BenefitObligation is null)
                    return Planned.Deny(409, "P1_EXECUTION_CLOSURE_STATE_INVALID");
                if (benefitAcceptance is null || !benefitAcceptance.AcceptedByBenefitOwner)
                    return Planned.Deny(409, "P1_EXECUTION_BENEFIT_ACCEPTANCE_REQUIRED");
                afterExecution = execution with
                {
                    State = "CLOSURE_IN_PROGRESS",
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionClosureStarted.v1", afterExecution, command, now,
                    new Dictionary<string,string>
                    {
                        ["state"] = "CLOSURE_IN_PROGRESS",
                        ["benefitId"] = before.BenefitObligation.BenefitId,
                        ["benefitAcceptanceEvidenceRef"] = benefitAcceptance.EvidenceRef
                    });
                break;

            case "executions.close":
                if (!State(execution, "CLOSURE_IN_PROGRESS"))
                    return Planned.Deny(409, "P1_EXECUTION_CLOSE_STATE_INVALID");
                afterExecution = execution with
                {
                    State = "CLOSED",
                    Version = nextVersion,
                    UpdatedAt = now,
                    CorrelationId = command.CorrelationId
                };
                events = One("ExecutionClosed.v1", afterExecution, command, now,
                    new Dictionary<string,string> { ["state"] = "CLOSED" });
                break;

            default:
                return Planned.Deny(503, "P1_EXECUTION_COMMAND_NOT_IMPLEMENTED");
        }

        var after = new ExecutionThreadEnvelopeWave10(afterExecution, afterBenefit);
        var audit = UserAudit(actor, ownership, afterExecution, command, policy, now);
        return Planned.Allow(new ExecutionCommitWave10(after, audit, events));
    }

    private static bool ValidateActorIdentityAndRole(
        AuthorityActor? actor,
        ExecutionCommandWave10 command,
        CommandPolicy policy,
        out (int Status, string Code) error)
    {
        if (actor is null
            || string.IsNullOrWhiteSpace(actor.PersonId)
            || string.IsNullOrWhiteSpace(actor.NetworkIdentity)
            || string.IsNullOrWhiteSpace(actor.IdentitySource)
            || string.IsNullOrWhiteSpace(actor.AssignmentId))
        {
            error = (401, "P1_IDENTITY_ASSIGNMENT_REQUIRED");
            return false;
        }

        var roles = actor.Roles.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length != 1
            || policy.RequiredRoles.Count != 1
            || !policy.RequiredRoles.Any(required => string.Equals(required, roles[0], StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_EXECUTION_COMMAND_ROLE_REQUIRED");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !actor.Scopes.Any(x => string.Equals(x, command.RequestedScope, StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_SCOPE_DENIED");
            return false;
        }

        error = default;
        return true;
    }

    private static bool ValidateAuthorityContext(
        AuthorityActor actor,
        ExecutionCommandWave10 command,
        CommandPolicy policy,
        ExecutionOwnershipEvidenceWave10 ownership,
        out (int Status, string Code) error)
    {
        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !string.Equals(command.RequestedScope, ownership.Scope, StringComparison.OrdinalIgnoreCase))
        {
            error = (403, "P1_EXECUTION_SCOPE_CONTEXT_MISMATCH");
            return false;
        }

        if (policy.RequiredRoles.Any(x => string.Equals(x, "EXECUTION_OWNER", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(actor.PersonId, ownership.OwnerPersonId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actor.AssignmentId, ownership.OwnerAssignmentId, StringComparison.OrdinalIgnoreCase))
            {
                error = (403, "P1_EXECUTION_OWNER_ASSIGNMENT_MISMATCH");
                return false;
            }
        }
        else if (policy.RequiredRoles.Any(x => string.Equals(x, "EXECUTION_COMPLETION_REVIEWER", StringComparison.OrdinalIgnoreCase))
                 && string.Equals(actor.PersonId, ownership.OwnerPersonId, StringComparison.OrdinalIgnoreCase))
        {
            error = (403, "SOD_EXECUTION_SELF_COMPLETION");
            return false;
        }

        error = default;
        return true;
    }

    private static ExecutionCommandWave10 Normalize(ExecutionCommandWave10 command) => command with
    {
        CommandName = command.CommandName?.Trim() ?? string.Empty,
        ExecutionId = command.ExecutionId?.Trim() ?? string.Empty,
        IdempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty,
        CorrelationId = command.CorrelationId?.Trim() ?? string.Empty,
        RequestedScope = TrimOrNull(command.RequestedScope),
        CompletionDossierRef = TrimOrNull(command.CompletionDossierRef),
        CompletionDecision = TrimOrNull(command.CompletionDecision)?.ToUpperInvariant(),
        ReviewNote = TrimOrNull(command.ReviewNote)
    };

    private static string Fingerprint(ExecutionCommandWave10 command, AuthorityActor actor)
    {
        var raw = string.Join('|', new[]
        {
            command.CommandName,
            command.ExecutionId,
            command.ExpectedVersion.ToString(),
            command.RequestedScope ?? string.Empty,
            command.ProgressPercent?.ToString() ?? string.Empty,
            command.CompletionDossierRef ?? string.Empty,
            command.CompletionDecision ?? string.Empty,
            command.ReviewNote ?? string.Empty,
            actor.PersonId,
            actor.AssignmentId
        });
        return Sha256Hex(raw);
    }

    private static bool State(ExecutionEnvelopeWave10 execution, string expected) =>
        string.Equals(execution.State, expected, StringComparison.Ordinal);

    private static IReadOnlyCollection<OutboxEnvelope> One(
        string eventName,
        ExecutionEnvelopeWave10 execution,
        ExecutionCommandWave10 command,
        DateTimeOffset now,
        IReadOnlyDictionary<string,string> payload) =>
        new[] { Event(eventName, execution.ExecutionId, execution.Version, command.CorrelationId, now, payload) };

    private static AuditEnvelope UserAudit(
        AuthorityActor actor,
        ExecutionOwnershipEvidenceWave10 ownership,
        ExecutionEnvelopeWave10 execution,
        ExecutionCommandWave10 command,
        CommandPolicy policy,
        DateTimeOffset now) =>
        new(
            "AUD-" + Guid.NewGuid().ToString("N"),
            actor.PersonId,
            actor.NetworkIdentity,
            actor.IdentitySource,
            actor.Roles,
            actor.AssignmentId,
            execution.ExecutionId,
            execution.Version,
            policy.RuleSet + "|OWNER_EVIDENCE:" + ownership.EvidenceRef + "@" + ownership.EvidenceVersion,
            now,
            command.CorrelationId,
            command.CommandName);

    private static OutboxEnvelope Event(
        string eventName,
        string aggregateId,
        long version,
        string correlationId,
        DateTimeOffset now,
        IReadOnlyDictionary<string,string> payload) =>
        new(
            "MSG-" + Guid.NewGuid().ToString("N"),
            eventName,
            aggregateId,
            version,
            correlationId,
            now,
            payload);

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Planned(bool Allowed, int Status, string Code, ExecutionCommitWave10? Commit, string? Detail = null)
    {
        public static Planned Allow(ExecutionCommitWave10 commit) => new(true, 200, "OK", commit);
        public static Planned Deny(int status, string code, string? detail = null) => new(false, status, code, null, detail);
    }
}
