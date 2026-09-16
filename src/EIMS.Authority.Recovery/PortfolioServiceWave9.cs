using System.Security.Cryptography;
using System.Text;

namespace EIMS.Authority.Recovery;

/// <summary>
/// Wave 9 server-authoritative Portfolio runtime derived from ACR-P0-008.
/// Portfolio eligibility is system-triggered only. User mutations require an exact
/// P3-resolved PORTFOLIO_MANAGER assignment; client role/scope values are never authority.
/// </summary>
public sealed class PortfolioServiceWave9(
    IPortfolioWave9Store store,
    ICommandPolicyCatalog? catalog = null,
    TimeProvider? clock = null)
{
    private readonly ICommandPolicyCatalog _catalog = catalog ?? new RecoveredApiCommandCatalogWave9();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<AuthorityResult> EvaluateEligibilityAsync(
        PortfolioEligibilityTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(trigger.SourceEventName, "IdeaApprovedForPortfolio.v1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(trigger.IdeaId)
            || trigger.ApprovedIdeaVersion <= 0
            || string.IsNullOrWhiteSpace(trigger.CorrelationId))
            return AuthorityResult.Deny(400, "P1_PORTFOLIO_ELIGIBILITY_EVENT_INVALID", trigger.CorrelationId ?? string.Empty);

        var fingerprint = $"{trigger.IdeaId.Trim()}|{trigger.ApprovedIdeaVersion}";
        var prior = await store.GetPortfolioIdempotencyAsync(
            "portfolio.evaluate-eligibility-from-approved-idea",
            trigger.IdeaId.Trim(),
            fingerprint,
            cancellationToken);
        if (prior is not null)
            return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = trigger.CorrelationId };

        if (!string.Equals(trigger.IdeaStatus?.Trim(), "APPROVED", StringComparison.OrdinalIgnoreCase))
            return AuthorityResult.Deny(409, "P1_PORTFOLIO_IDEA_NOT_APPROVED", trigger.CorrelationId);
        if (trigger.HardReadinessGaps != 0)
            return AuthorityResult.Deny(409, "P1_PORTFOLIO_HARD_READINESS_GAPS", trigger.CorrelationId);
        if (trigger.PolicyExcluded)
            return AuthorityResult.Deny(409, "P1_PORTFOLIO_POLICY_EXCLUDED", trigger.CorrelationId);

        var existing = await store.GetByIdeaVersionAsync(trigger.IdeaId.Trim(), trigger.ApprovedIdeaVersion, cancellationToken);
        if (existing is not null)
            return new AuthorityResult(
                200, "P1_PORTFOLIO_ELIGIBILITY_ALREADY_MATERIALIZED", true, false, true,
                existing.ThreadVersion, trigger.CorrelationId,
                Array.Empty<string>());

        var now = _clock.GetUtcNow();
        var candidateId = "PIC-" + Sha256Hex(fingerprint)[..20].ToUpperInvariant();
        var candidate = new PortfolioCandidateEnvelope(
            candidateId,
            trigger.IdeaId.Trim(),
            trigger.ApprovedIdeaVersion,
            "UNASSIGNED_CANDIDATE",
            1,
            now,
            trigger.CorrelationId);
        var thread = new PortfolioThreadEnvelope(candidate, null, null, null, null, 1);

        var audit = SystemAudit(candidateId, 1, trigger.CorrelationId, now,
            "portfolio.evaluate-eligibility-from-approved-idea", "P1-PORTFOLIO-ELIGIBILITY-ACR-P0-008-1.0");
        var outbox = new[]
        {
            Event("PortfolioEligibilityEvaluated.v1", candidateId, 1, trigger.CorrelationId, now,
                new Dictionary<string,string>
                {
                    ["ideaId"] = candidate.IdeaId,
                    ["approvedIdeaVersion"] = candidate.ApprovedIdeaVersion.ToString(),
                    ["result"] = "PASS"
                }),
            Event("PortfolioIntakeCandidateCreated.v1", candidateId, 1, trigger.CorrelationId, now,
                new Dictionary<string,string>
                {
                    ["candidateId"] = candidateId,
                    ["ideaId"] = candidate.IdeaId,
                    ["approvedIdeaVersion"] = candidate.ApprovedIdeaVersion.ToString(),
                    ["state"] = candidate.State
                })
        };

        return await store.CommitEligibilityAsync(
            new PortfolioEligibilityRequestWave9(trigger with { IdeaId = trigger.IdeaId.Trim() }, fingerprint),
            new PortfolioCommitWave9(thread, audit, outbox),
            cancellationToken);
    }

    public async ValueTask<AuthorityResult> ExecuteAsync(
        PortfolioCommandWave9 command,
        AuthorityActor? actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ValidateActor(actor, command, out var actorError))
            return AuthorityResult.Deny(actorError.Status, actorError.Code, command.CorrelationId);
        actor = actor!;

        if (string.IsNullOrWhiteSpace(command.CommandName)
            || string.IsNullOrWhiteSpace(command.CandidateId)
            || string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || string.IsNullOrWhiteSpace(command.CorrelationId)
            || command.ExpectedThreadVersion <= 0)
            return AuthorityResult.Deny(400, "P1_PORTFOLIO_COMMAND_INVALID", command.CorrelationId ?? string.Empty);

        if (!_catalog.TryGet(command.CommandName.Trim(), out var policy)
            || !policy.MutationContractRecovered
            || !policy.RuleContractRecovered
            || !policy.EventContractRecovered)
            return AuthorityResult.Deny(503, "P1_PORTFOLIO_COMMAND_NOT_BOUND", command.CorrelationId);

        var normalized = Normalize(command);
        var fingerprint = Fingerprint(normalized, actor);
        var prior = await store.GetPortfolioIdempotencyAsync(
            normalized.CommandName,
            normalized.CandidateId,
            normalized.IdempotencyKey,
            cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                return AuthorityResult.Deny(409, "P1_IDEMPOTENCY_CONFLICT", normalized.CorrelationId);
            return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = normalized.CorrelationId };
        }

        var before = await store.GetByCandidateAsync(normalized.CandidateId, cancellationToken);
        if (before is null)
            return AuthorityResult.Deny(404, "P1_PORTFOLIO_CANDIDATE_NOT_FOUND", normalized.CorrelationId);
        if (before.ThreadVersion != normalized.ExpectedThreadVersion)
            return AuthorityResult.Deny(409, "P1_PORTFOLIO_VERSION_CONFLICT", normalized.CorrelationId);

        var result = Plan(normalized, actor, before, policy);
        if (!result.Allowed || result.Commit is null)
            return AuthorityResult.Deny(result.Status, result.Code, normalized.CorrelationId, result.Detail);

        return await store.CommitPortfolioCommandAsync(
            new PortfolioCommandRequestWave9(normalized, actor, before, policy, fingerprint),
            result.Commit,
            cancellationToken);
    }

    private Planned Plan(
        PortfolioCommandWave9 command,
        AuthorityActor actor,
        PortfolioThreadEnvelope before,
        CommandPolicy policy)
    {
        var now = _clock.GetUtcNow();
        var nextVersion = before.ThreadVersion + 1;
        PortfolioThreadEnvelope after;
        IReadOnlyCollection<OutboxEnvelope> events;

        switch (command.CommandName)
        {
            case "portfolio.assign-candidate":
                if (!string.Equals(before.Candidate.State, "UNASSIGNED_CANDIDATE", StringComparison.Ordinal)
                    || before.Assignment is not null)
                    return Planned.Deny(409, "P1_PORTFOLIO_ASSIGN_STATE_INVALID");
                if (string.IsNullOrWhiteSpace(command.PortfolioId))
                    return Planned.Deny(400, "P1_PORTFOLIO_ID_REQUIRED");

                var assignment = new PortfolioAssignmentEnvelope(
                    "PA-" + Guid.NewGuid().ToString("N"), before.Candidate.CandidateId,
                    command.PortfolioId!, "PENDING", 1, now, command.CorrelationId);
                after = before with
                {
                    Candidate = before.Candidate with { State = "PENDING_ASSIGNMENT", Version = before.Candidate.Version + 1 },
                    Assignment = assignment,
                    ThreadVersion = nextVersion
                };
                events = One("PortfolioCandidateAssigned.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["candidateId"] = after.Candidate.CandidateId,
                        ["assignmentId"] = assignment.AssignmentId,
                        ["portfolioId"] = assignment.PortfolioId,
                        ["state"] = assignment.State
                    });
                break;

            case "portfolio.membership-decision":
                if (before.Assignment is null || before.Membership is not null
                    || !string.Equals(before.Assignment.State, "PENDING", StringComparison.Ordinal)
                    || !string.Equals(before.Candidate.State, "PENDING_ASSIGNMENT", StringComparison.Ordinal))
                    return Planned.Deny(409, "P1_PORTFOLIO_MEMBERSHIP_STATE_INVALID");
                if (command.MembershipDecision is not ("ACCEPTED" or "REJECTED" or "DEFERRED"))
                    return Planned.Deny(400, "P1_PORTFOLIO_MEMBERSHIP_DECISION_INVALID");

                var membership = new PortfolioMembershipEnvelope(
                    "PM-" + Guid.NewGuid().ToString("N"), before.Candidate.CandidateId,
                    before.Assignment.AssignmentId, before.Assignment.PortfolioId,
                    command.MembershipDecision!, 1, now, actor.PersonId, actor.AssignmentId, command.CorrelationId);
                after = before with
                {
                    Candidate = before.Candidate with { State = command.MembershipDecision!, Version = before.Candidate.Version + 1 },
                    Assignment = before.Assignment with { State = "DECIDED", Version = before.Assignment.Version + 1 },
                    Membership = membership,
                    ThreadVersion = nextVersion
                };
                var membershipEvent = command.MembershipDecision switch
                {
                    "ACCEPTED" => "PortfolioMembershipAccepted.v1",
                    "REJECTED" => "PortfolioMembershipRejected.v1",
                    _ => "PortfolioMembershipDeferred.v1"
                };
                events = One(membershipEvent, after, command, now,
                    new Dictionary<string,string>
                    {
                        ["candidateId"] = after.Candidate.CandidateId,
                        ["membershipId"] = membership.MembershipId,
                        ["portfolioId"] = membership.PortfolioId,
                        ["status"] = membership.Status
                    });
                break;

            case "portfolio.generate-execution-recommendation":
                if (before.Membership is null
                    || !string.Equals(before.Membership.Status, "ACCEPTED", StringComparison.Ordinal)
                    || before.Recommendation is not null)
                    return Planned.Deny(409, "P1_PORTFOLIO_ACCEPTED_MEMBERSHIP_REQUIRED");

                var recommendation = new ExecutionRecommendationEnvelope(
                    "ER-" + Guid.NewGuid().ToString("N"), before.Candidate.CandidateId,
                    before.Membership.MembershipId, "DRAFT", 1, null, false, null, now, command.CorrelationId);
                after = before with { Recommendation = recommendation, ThreadVersion = nextVersion };
                events = One("ExecutionRecommendationGenerated.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["candidateId"] = after.Candidate.CandidateId,
                        ["recommendationId"] = recommendation.RecommendationId,
                        ["state"] = recommendation.State
                    });
                break;

            case "portfolio.approve-execution-recommendation":
                if (!RecommendationMatches(before, command, "DRAFT"))
                    return Planned.Deny(409, "P1_PORTFOLIO_DRAFT_RECOMMENDATION_REQUIRED");
                if (string.IsNullOrWhiteSpace(command.GovernanceDecisionRef))
                    return Planned.Deny(400, "P1_PORTFOLIO_GOVERNANCE_DECISION_REF_REQUIRED");

                var approved = before.Recommendation! with
                {
                    State = "APPROVED",
                    Version = before.Recommendation!.Version + 1,
                    GovernanceDecisionRef = command.GovernanceDecisionRef
                };
                after = before with { Recommendation = approved, ThreadVersion = nextVersion };
                events = One("ExecutionRecommendationApproved.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["recommendationId"] = approved.RecommendationId,
                        ["state"] = approved.State,
                        ["governanceDecisionRef"] = approved.GovernanceDecisionRef!
                    });
                break;

            case "portfolio.bind-approved-baseline":
                if (!RecommendationMatches(before, command, "APPROVED") || before.Recommendation!.BaselineApproved)
                    return Planned.Deny(409, "P1_PORTFOLIO_APPROVED_RECOMMENDATION_REQUIRED");
                if (string.IsNullOrWhiteSpace(command.ApprovedBaselineRef))
                    return Planned.Deny(400, "P1_PORTFOLIO_APPROVED_BASELINE_REF_REQUIRED");

                var baselined = before.Recommendation! with
                {
                    Version = before.Recommendation!.Version + 1,
                    BaselineApproved = true,
                    ApprovedBaselineRef = command.ApprovedBaselineRef
                };
                after = before with { Recommendation = baselined, ThreadVersion = nextVersion };
                events = One("PortfolioBaselineBoundToRecommendation.v1", after, command, now,
                    new Dictionary<string,string>
                    {
                        ["recommendationId"] = baselined.RecommendationId,
                        ["approvedBaselineRef"] = baselined.ApprovedBaselineRef!
                    });
                break;

            case "portfolio.request-execution-handoff":
                if (!RecommendationMatches(before, command, "APPROVED")
                    || !before.Recommendation!.BaselineApproved
                    || string.IsNullOrWhiteSpace(before.Recommendation.ApprovedBaselineRef))
                    return Planned.Deny(409, "P1_PORTFOLIO_BASELINED_RECOMMENDATION_REQUIRED");
                if (before.Execution is not null)
                    return Planned.Deny(409, "P1_PORTFOLIO_EXECUTION_ALREADY_CREATED");

                var execution = new ExecutionHandoffEnvelope(
                    "EX-" + Guid.NewGuid().ToString("N"), before.Recommendation.RecommendationId,
                    before.Candidate.CandidateId, before.Candidate.IdeaId, before.Candidate.ApprovedIdeaVersion,
                    "PLANNING", 1, now, command.CorrelationId);
                after = before with { Execution = execution, ThreadVersion = nextVersion };
                events = new[]
                {
                    Event("ExecutionHandoffRequested.v1", after.Candidate.CandidateId, nextVersion,
                        command.CorrelationId, now, new Dictionary<string,string>
                        {
                            ["candidateId"] = after.Candidate.CandidateId,
                            ["recommendationId"] = execution.RecommendationId,
                            ["approvedBaselineRef"] = before.Recommendation.ApprovedBaselineRef!
                        }),
                    Event("ExecutionCreatedFromRecommendation.v1", execution.ExecutionId, 1,
                        command.CorrelationId, now, new Dictionary<string,string>
                        {
                            ["executionId"] = execution.ExecutionId,
                            ["recommendationId"] = execution.RecommendationId,
                            ["ideaId"] = execution.IdeaId,
                            ["approvedIdeaVersion"] = execution.ApprovedIdeaVersion.ToString(),
                            ["state"] = execution.State
                        })
                };
                break;

            default:
                return Planned.Deny(503, "P1_PORTFOLIO_COMMAND_NOT_IMPLEMENTED");
        }

        var audit = UserAudit(actor, after.Candidate.CandidateId, nextVersion,
            command.CorrelationId, now, command.CommandName, policy.RuleSet);
        return Planned.Allow(new PortfolioCommitWave9(after, audit, events));
    }

    private static bool ValidateActor(AuthorityActor? actor, PortfolioCommandWave9 command, out (int Status, string Code) error)
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
        if (roles.Length != 1 || !string.Equals(roles[0], "PORTFOLIO_MANAGER", StringComparison.OrdinalIgnoreCase))
        {
            error = (403, "P1_PORTFOLIO_MANAGER_AUTHORITY_REQUIRED");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.RequestedScope)
            && !actor.Scopes.Any(x => string.Equals(x, command.RequestedScope.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            error = (403, "P1_SCOPE_DENIED");
            return false;
        }

        error = default;
        return true;
    }

    private static PortfolioCommandWave9 Normalize(PortfolioCommandWave9 command) => command with
    {
        CommandName = command.CommandName?.Trim().ToLowerInvariant() ?? string.Empty,
        CandidateId = command.CandidateId?.Trim() ?? string.Empty,
        IdempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty,
        CorrelationId = command.CorrelationId?.Trim() ?? string.Empty,
        PortfolioId = TrimOrNull(command.PortfolioId),
        MembershipDecision = TrimOrNull(command.MembershipDecision)?.ToUpperInvariant(),
        RecommendationId = TrimOrNull(command.RecommendationId),
        GovernanceDecisionRef = TrimOrNull(command.GovernanceDecisionRef),
        ApprovedBaselineRef = TrimOrNull(command.ApprovedBaselineRef),
        RequestedScope = TrimOrNull(command.RequestedScope)
    };

    public static string Fingerprint(PortfolioCommandWave9 command, AuthorityActor actor)
    {
        var material = string.Join("|", new[]
        {
            command.CommandName, command.CandidateId, command.ExpectedThreadVersion.ToString(),
            command.PortfolioId ?? string.Empty, command.MembershipDecision ?? string.Empty,
            command.RecommendationId ?? string.Empty, command.GovernanceDecisionRef ?? string.Empty,
            command.ApprovedBaselineRef ?? string.Empty, command.RequestedScope ?? string.Empty,
            actor.PersonId, actor.AssignmentId
        });
        return Sha256Hex(material);
    }

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool RecommendationMatches(PortfolioThreadEnvelope before, PortfolioCommandWave9 command, string state) =>
        before.Recommendation is not null
        && !string.IsNullOrWhiteSpace(command.RecommendationId)
        && string.Equals(before.Recommendation.RecommendationId, command.RecommendationId, StringComparison.Ordinal)
        && string.Equals(before.Recommendation.State, state, StringComparison.Ordinal);

    private static IReadOnlyCollection<OutboxEnvelope> One(
        string eventName,
        PortfolioThreadEnvelope after,
        PortfolioCommandWave9 command,
        DateTimeOffset now,
        IReadOnlyDictionary<string,string> payload) =>
        new[] { Event(eventName, after.Candidate.CandidateId, after.ThreadVersion, command.CorrelationId, now, payload) };

    private static AuditEnvelope UserAudit(
        AuthorityActor actor, string aggregateId, long version, string correlationId,
        DateTimeOffset now, string commandName, string ruleSet) =>
        new("AUD-" + Guid.NewGuid().ToString("N"), actor.PersonId, actor.NetworkIdentity, actor.IdentitySource,
            actor.Roles.ToArray(), actor.AssignmentId, aggregateId, version, ruleSet, now, correlationId, commandName);

    private static AuditEnvelope SystemAudit(
        string aggregateId, long version, string correlationId, DateTimeOffset now,
        string commandName, string ruleSet) =>
        new("AUD-" + Guid.NewGuid().ToString("N"), "SYSTEM", "SYSTEM_SERVICE", "SYSTEM_EVENT",
            new[] { "SYSTEM_SERVICE" }, "SYSTEM", aggregateId, version, ruleSet, now, correlationId, commandName);

    private static OutboxEnvelope Event(
        string eventName, string aggregateId, long version, string correlationId,
        DateTimeOffset now, IReadOnlyDictionary<string,string> payload) =>
        new("MSG-" + Guid.NewGuid().ToString("N"), eventName, aggregateId, version, correlationId, now, payload);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Planned(bool Allowed, int Status, string Code, string? Detail, PortfolioCommitWave9? Commit)
    {
        public static Planned Deny(int status, string code, string? detail = null) => new(false, status, code, detail, null);
        public static Planned Allow(PortfolioCommitWave9 commit) => new(true, 200, "PASS", null, commit);
    }
}
